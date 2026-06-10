// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Enums.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Startup hook for podcast generation jobs persisted by F.1 (PodcastJob).
/// Scans the PodcastJobs table on app launch and marks any row still in
/// <see cref="PodcastJobStatus.Running"/> or <see cref="PodcastJobStatus.Pending"/>
/// as <see cref="PodcastJobStatus.Interrupted"/> — the previous app
/// instance died before that job finished, so without the orphan sweep
/// the row would advertise itself as Running forever.
/// </summary>
/// <remarks>
/// <para>
/// workspace-nk06 (F.2). This service does NOT yet re-enqueue interrupted
/// jobs for resume; that is F.4 (workspace-ur2s). For now it only
/// honest-up the table so the launcher badge (F.5) doesn't show a
/// phantom "still generating" badge after a crash.
/// </para>
/// <para>
/// Runs StartAsync work on a background task so a slow DB doesn't block
/// host startup; failures are logged but never thrown — orphan cleanup
/// is best-effort.
/// </para>
/// </remarks>
internal sealed class PodcastJobLifecycleService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PodcastJobLifecycleService> _logger;

    public PodcastJobLifecycleService(
        IServiceScopeFactory scopeFactory,
        ILogger<PodcastJobLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// The background sweep dispatched by <see cref="StartAsync"/>. Exposed so
    /// tests can await completion deterministically; polling the table from a
    /// second thread races the sweep on a shared in-memory SQLite connection,
    /// which is not thread-safe (workspace-ml8j). Null until StartAsync runs.
    /// Never faults — the wrapper swallows and logs all sweep exceptions.
    /// </summary>
    internal Task? SweepTask { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SweepTask = Task.Run(
            async () =>
            {
                try
                {
                    await SweepOrphanedJobsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Host is shutting down before the sweep completed; nothing to do.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Podcast job orphan sweep failed (non-fatal)");
                }
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SweepOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPodcastJobRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var active = await repo.GetActiveJobsAsync(cancellationToken).ConfigureAwait(false);
        if (active.Count == 0)
        {
            return;
        }

        foreach (var job in active)
        {
            job.MarkInterrupted("App restarted before generation completed");
            await repo.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "PodcastJob {JobId} ({Title}) marked Interrupted — was {Phase} when previous app instance exited",
                job.Id,
                job.CollectionTitle,
                job.Phase);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
