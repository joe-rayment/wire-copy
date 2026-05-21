// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Entities.Collections;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Tracks the single in-process podcast generation job and forwards live
/// progress events to subscribers. Owns the running <see cref="Task{PodcastResult}"/>
/// and the linked <see cref="CancellationTokenSource"/> so the modal can
/// detach (workspace-vkhr) without cancelling the underlying work, and a
/// later reattach can subscribe to the live stream without restarting
/// generation.
/// </summary>
/// <remarks>
/// <para>
/// Phase D of workspace-rn4j. The manager intentionally supports only a
/// single active job — multi-job is out of scope until the persistent
/// background-job feature lands in Phase F (workspace-ur0e).
/// </para>
/// <para>
/// Lifecycle invariant: callers MUST call <see cref="Clear"/> after the
/// <see cref="Completed"/> event fires so a follow-up run can be started.
/// The manager does not call <see cref="Clear"/> itself because the
/// consumer owns presentation of the result (and may need the snapshot
/// to render a final frame).
/// </para>
/// </remarks>
public interface IPodcastBackgroundJobManager
{
    /// <summary>
    /// True while a job is registered and has not been cleared.
    /// </summary>
    bool HasActiveJob { get; }

    /// <summary>
    /// Most recent <see cref="PodcastProgress"/> snapshot, or <c>null</c>
    /// if no event has been reported yet. Reads use a volatile barrier so
    /// the status-bar renderer can safely read from any thread.
    /// </summary>
    PodcastProgress? LastSnapshot { get; }

    /// <summary>
    /// The collection the active job is generating from, or <c>null</c>
    /// when no job is registered.
    /// </summary>
    Collection? Collection { get; }

    /// <summary>
    /// Resolved destination paths for the active job (workspace-zh3u),
    /// or <c>null</c> when no job is registered or targets resolution
    /// failed at startup.
    /// </summary>
    PodcastTargets? Targets { get; }

    /// <summary>
    /// The running generation task, or <c>null</c> when no job is
    /// registered. Consumers can await this to receive the
    /// <see cref="PodcastResult"/> directly without subscribing to
    /// <see cref="Completed"/>.
    /// </summary>
    Task<PodcastResult>? CurrentJobTask { get; }

    /// <summary>
    /// Fired on every <see cref="ReportProgress"/> call. Subscribers must
    /// be cheap — the orchestrator delivers progress events on the
    /// generation thread and a slow handler will throttle TTS throughput.
    /// </summary>
    event EventHandler<PodcastProgress>? ProgressUpdated;

    /// <summary>
    /// Fired exactly once when the registered task completes (success,
    /// failure, or cancellation). The argument is the awaited result
    /// (or <c>null</c> on cancellation / fault). Subscribers must call
    /// <see cref="Clear"/> after handling the event so a new job can run.
    /// </summary>
    event EventHandler<PodcastResult?>? Completed;

    /// <summary>
    /// Registers a generation job. Throws
    /// <see cref="InvalidOperationException"/> when a job is already
    /// registered — the manager enforces a single-active-job invariant.
    /// </summary>
    /// <param name="collection">Collection the job is generating from.</param>
    /// <param name="targets">Resolved destination paths.</param>
    /// <param name="jobTask">The running orchestrator task.</param>
    /// <param name="cts">Linked cancellation source the manager will
    /// trigger on <see cref="RequestCancellation"/>.</param>
    void StartJob(
        Collection collection,
        PodcastTargets? targets,
        Task<PodcastResult> jobTask,
        CancellationTokenSource cts);

    /// <summary>
    /// Records the latest progress snapshot and fans the event out to
    /// subscribers. Called by the modal's <see cref="System.Progress{T}"/>
    /// callback so detach + reattach both see the same live stream.
    /// </summary>
    void ReportProgress(PodcastProgress progress);

    /// <summary>
    /// Triggers cancellation on the registered <see cref="CancellationTokenSource"/>
    /// if one is active. Returns true when a cancellation was issued, false
    /// when no job is registered or the source was already cancelled.
    /// </summary>
    bool RequestCancellation();

    /// <summary>
    /// Releases all state for the active job. Must be called by the
    /// consumer after the <see cref="Completed"/> event fires (or after
    /// the consumer awaits <see cref="CurrentJobTask"/> directly).
    /// Safe to call when no job is registered (no-op).
    /// </summary>
    void Clear();
}
