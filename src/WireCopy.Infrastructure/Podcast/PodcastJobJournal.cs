// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Entities.Podcast;
using WireCopy.Domain.Enums.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Writes <see cref="PodcastJob"/> rows on behalf of the orchestrator
/// (workspace-hzjs, F.3). One journal per podcast generation attempt:
/// created on entry, fed via <see cref="ReportProgress"/> while the
/// pipeline runs, and finalized once with <see cref="FinishAsync"/>,
/// <see cref="MarkCancelledAsync"/>, or <see cref="MarkFailedAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator is singleton-scoped; the repository is request-scoped.
/// The journal therefore takes an <see cref="IServiceScopeFactory"/> and
/// creates a fresh scope for each DB hit. Progress snapshots are debounced
/// so a stream of byte-level events doesn't hammer SQLite — we keep the
/// latest <see cref="PodcastProgress"/> in memory and write at most once
/// per <see cref="_debounceInterval"/>.
/// </para>
/// <para>
/// All DB writes are fire-and-forget on the threadpool; failures are
/// logged at Debug and swallowed because losing a journal row should
/// never tear down a real podcast generation. The terminal write
/// (Finish/Cancelled/Failed) IS awaited so the row reflects the final
/// state by the time the orchestrator returns.
/// </para>
/// </remarks>
internal sealed class PodcastJobJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly Guid _jobId;
    private readonly TimeSpan _debounceInterval;
    private readonly Lock _lock = new();
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private Task _inflightWrite = Task.CompletedTask;
    private bool _finalized;

    private PodcastJobJournal(
        IServiceScopeFactory scopeFactory,
        Guid jobId,
        TimeSpan debounceInterval,
        ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _jobId = jobId;
        _debounceInterval = debounceInterval;
        _logger = logger;
    }

    public Guid JobId => _jobId;

    /// <summary>
    /// Creates a new Running PodcastJob row and returns a journal handle
    /// bound to it. The row is persisted before the journal is returned,
    /// so the orphan sweep (workspace-nk06) can pick it up immediately
    /// if the app crashes after this call.
    /// </summary>
    public static async Task<PodcastJobJournal> CreateAsync(
        IServiceScopeFactory scopeFactory,
        Collection collection,
        string? targetLocalPath,
        string? targetFeedUrl,
        TimeSpan debounceInterval,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(logger);

        var job = PodcastJob.Start(collection.Id, collection.Name, targetLocalPath, targetFeedUrl);

        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPodcastJobRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await repo.AddAsync(job, cancellationToken).ConfigureAwait(false);
        await uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PodcastJobJournal(scopeFactory, job.Id, debounceInterval, logger);
    }

    /// <summary>
    /// Records the latest progress snapshot. Synchronous and cheap — the
    /// actual DB write is dispatched on the threadpool and debounced.
    /// </summary>
    public void ReportProgress(PodcastProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (_finalized)
        {
            return;
        }

        PodcastProgress snapshot;
        bool shouldFlush;
        lock (_lock)
        {
            snapshot = progress;
            var sinceLast = DateTime.UtcNow - _lastFlushUtc;
            shouldFlush = sinceLast >= _debounceInterval && _inflightWrite.IsCompleted;
            if (!shouldFlush)
            {
                return;
            }

            _lastFlushUtc = DateTime.UtcNow;
            _inflightWrite = Task.Run(() => WriteSnapshotAsync(snapshot, CancellationToken.None));
        }
    }

    /// <summary>
    /// Synchronously writes the latest snapshot, awaits any inflight
    /// debounced write, then marks the row with the terminal status
    /// derived from <paramref name="result"/>.
    /// </summary>
    public async Task FinishAsync(PodcastResult result, PodcastProgress? lastSnapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        _finalized = true;
        await DrainPendingAsync().ConfigureAwait(false);

        var status = MapStatus(result);
        var errorClass = status switch
        {
            PodcastJobStatus.FullSuccess => null,
            PodcastJobStatus.PartialSuccess => "Partial",
            _ => result.FailureDetail?.FailureClass.ToString() ?? "Failed",
        };
        var errorMessage = status switch
        {
            PodcastJobStatus.FullSuccess => null,
            _ => result.ErrorMessage ?? result.FailureDetail?.RawMessage,
        };

        await UpdateAsync(
            job => job.Finish(status, errorClass, errorMessage),
            lastSnapshot,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Marks the job row Cancelled.</summary>
    public async Task MarkCancelledAsync(PodcastProgress? lastSnapshot, CancellationToken cancellationToken)
    {
        _finalized = true;
        await DrainPendingAsync().ConfigureAwait(false);
        await UpdateAsync(
            job => job.Finish(PodcastJobStatus.Cancelled, "Cancelled", "User cancelled generation"),
            lastSnapshot,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Marks the job row TotalFailure with the exception's message.</summary>
    public async Task MarkFailedAsync(Exception exception, PodcastProgress? lastSnapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _finalized = true;
        await DrainPendingAsync().ConfigureAwait(false);
        await UpdateAsync(
            job => job.Finish(PodcastJobStatus.TotalFailure, exception.GetType().Name, exception.Message),
            lastSnapshot,
            cancellationToken)
            .ConfigureAwait(false);
    }

    internal static PodcastJobPhase MapPhase(PodcastPhase phase) => phase switch
    {
        PodcastPhase.CachingContent => PodcastJobPhase.Extraction,
        PodcastPhase.GeneratingAudio => PodcastJobPhase.Synthesis,
        PodcastPhase.AssemblingAudio => PodcastJobPhase.Assembly,
        PodcastPhase.Publishing => PodcastJobPhase.Publish,
        _ => PodcastJobPhase.NotStarted,
    };

    internal static PodcastJobStatus MapStatus(PodcastResult result)
    {
        if (result.Classification is PodcastResultClassification.TotalFailure)
        {
            return PodcastJobStatus.TotalFailure;
        }

        if (result.Classification is PodcastResultClassification.PartialSuccess)
        {
            return PodcastJobStatus.PartialSuccess;
        }

        if (result.Classification is PodcastResultClassification.FullSuccess)
        {
            return PodcastJobStatus.FullSuccess;
        }

        if (!result.Success)
        {
            return PodcastJobStatus.TotalFailure;
        }

        // Success=true but no explicit classification — infer.
        if (result.ArticlesFailed > 0 || string.IsNullOrEmpty(result.FeedUrl))
        {
            return PodcastJobStatus.PartialSuccess;
        }

        return PodcastJobStatus.FullSuccess;
    }

    private async Task DrainPendingAsync()
    {
        Task pending;
        lock (_lock)
        {
            pending = _inflightWrite;
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // Background snapshot writes log + swallow on their own.
        }
    }

    private async Task WriteSnapshotAsync(PodcastProgress snapshot, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IPodcastJobRepository>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var job = await repo.GetByIdAsync(_jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                _logger.LogDebug("PodcastJob {JobId} disappeared during snapshot write", _jobId);
                return;
            }

            job.RecordProgress(MapPhase(snapshot.Phase), JsonSerializer.Serialize(snapshot, JsonOptions));
            await repo.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            await uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PodcastJob snapshot write failed (non-fatal)");
        }
    }

    private async Task UpdateAsync(
        Action<PodcastJob> mutate,
        PodcastProgress? lastSnapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IPodcastJobRepository>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var job = await repo.GetByIdAsync(_jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                _logger.LogWarning("PodcastJob {JobId} disappeared before finalize", _jobId);
                return;
            }

            if (lastSnapshot is not null && job.Status == PodcastJobStatus.Running)
            {
                // Make sure the final snapshot survives even if the debounce
                // had a write in flight at exit.
                job.RecordProgress(MapPhase(lastSnapshot.Phase), JsonSerializer.Serialize(lastSnapshot, JsonOptions));
            }

            mutate(job);

            await repo.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            await uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PodcastJob {JobId} finalize failed (non-fatal)", _jobId);
        }
    }
}
