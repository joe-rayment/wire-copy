// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces.Podcast;

/// <summary>
/// workspace-frpl.1 (B0) — a single process-wide mutex that guarantees at most
/// ONE podcast generation runs at a time, acquired at the moment generation
/// STARTS (not at modal-detach). Every producer — the manual progress modal and
/// the scheduler's recipe runs — must hold a lease for the lifetime of its
/// <c>GeneratePodcastAsync</c> call, so they can never contend for the shared
/// foreground Playwright page or write the same output M4B concurrently.
/// </summary>
/// <remarks>
/// The lease's lifetime is tied to the generation TASK, not the UI: a manual run
/// that detaches keeps its lease until the background task completes, and a
/// scheduled run that finds the gate held simply defers to a later tick.
/// </remarks>
public interface IPodcastGenerationGate
{
    /// <summary>True while a generation lease is currently held.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// Tries to take the gate without blocking. Returns true and yields a lease
    /// the caller MUST dispose when generation completes; returns false (lease
    /// null) when a generation is already in progress.
    /// </summary>
    bool TryAcquire(out IDisposable? lease);

    /// <summary>
    /// Waits for the gate and returns a lease the caller must dispose. Honours
    /// the cancellation token.
    /// </summary>
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
