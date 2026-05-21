// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Default in-memory implementation of <see cref="IPodcastBackgroundJobManager"/>
/// (workspace-vkhr Phase D). Owns the single active podcast generation job
/// so the progress modal can detach + reattach without restarting work.
/// </summary>
/// <remarks>
/// Thread-safety: mutation of the registration tuple (collection / targets /
/// task / cts) happens under <see cref="_lock"/>; the latest progress
/// snapshot uses <see cref="Volatile.Write"/> / <see cref="Volatile.Read"/>
/// so the status-bar renderer can read from any thread without acquiring
/// the lock. Event invocation is performed outside the lock to avoid
/// re-entrance deadlocks if a subscriber synchronously inspects the
/// manager state in its handler.
/// </remarks>
internal sealed class PodcastBackgroundJobManager : IPodcastBackgroundJobManager
{
    private readonly object _lock = new();
    private PodcastProgress? _lastSnapshot;
    private Collection? _collection;
    private PodcastTargets? _targets;
    private Task<PodcastResult>? _currentJobTask;
    private CancellationTokenSource? _cts;
    private bool _hasActiveJob;

    /// <inheritdoc />
    public event EventHandler<PodcastProgress>? ProgressUpdated;

    /// <inheritdoc />
    public event EventHandler<PodcastResult?>? Completed;

    /// <inheritdoc />
    public bool HasActiveJob
    {
        get
        {
            lock (_lock)
            {
                return _hasActiveJob;
            }
        }
    }

    /// <inheritdoc />
    public PodcastProgress? LastSnapshot => Volatile.Read(ref _lastSnapshot);

    /// <inheritdoc />
    public Collection? Collection
    {
        get
        {
            lock (_lock)
            {
                return _collection;
            }
        }
    }

    /// <inheritdoc />
    public PodcastTargets? Targets
    {
        get
        {
            lock (_lock)
            {
                return _targets;
            }
        }
    }

    /// <inheritdoc />
    public Task<PodcastResult>? CurrentJobTask
    {
        get
        {
            lock (_lock)
            {
                return _currentJobTask;
            }
        }
    }

    /// <inheritdoc />
    public void StartJob(
        Collection collection,
        PodcastTargets? targets,
        Task<PodcastResult> jobTask,
        CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(jobTask);
        ArgumentNullException.ThrowIfNull(cts);

        lock (_lock)
        {
            if (_hasActiveJob)
            {
                throw new InvalidOperationException(
                    "A podcast generation job is already active; call Clear() after the previous job completes before starting a new one.");
            }

            _collection = collection;
            _targets = targets;
            _currentJobTask = jobTask;
            _cts = cts;
            _hasActiveJob = true;
        }

        // Wire the Completed event to fire when the underlying task ends.
        // Using ContinueWith (rather than awaiting inside StartJob) so the
        // call returns promptly and the caller's progress loop can run.
        // The continuation reads the result with no exception bubbling —
        // OperationCanceledException / faults surface as a null result.
        jobTask.ContinueWith(
            t =>
            {
                PodcastResult? result = null;
                if (t.IsCompletedSuccessfully)
                {
                    result = t.Result;
                }

                // Fire outside the lock to avoid deadlock if a handler
                // re-enters the manager (e.g. via HasActiveJob).
                Completed?.Invoke(this, result);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    /// <inheritdoc />
    public void ReportProgress(PodcastProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // Volatile.Write makes the snapshot visible to any reader thread
        // without locking — the status-bar renderer hits this path on
        // every render frame.
        Volatile.Write(ref _lastSnapshot, progress);

        // Fan out. Snapshot the delegate locally so a concurrent
        // unsubscribe doesn't NRE us.
        ProgressUpdated?.Invoke(this, progress);
    }

    /// <inheritdoc />
    public bool RequestCancellation()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _cts;
        }

        if (cts is null || cts.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // CTS was disposed between our null check and the Cancel
            // call — racing with Clear(). Treat as already-cancelled.
            return false;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _collection = null;
            _targets = null;
            _currentJobTask = null;
            _cts = null;
            _hasActiveJob = false;
        }

        Volatile.Write(ref _lastSnapshot, null);
    }
}
