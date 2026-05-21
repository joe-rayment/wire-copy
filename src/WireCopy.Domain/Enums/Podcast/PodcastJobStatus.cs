// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Podcast;

/// <summary>
/// Lifecycle state of a podcast generation job in the PodcastJobs table.
/// Stored as int by EF Core; ordering of members must be append-only so
/// migrations stay backwards-compatible.
/// </summary>
public enum PodcastJobStatus
{
    /// <summary>Row written but the orchestrator has not started yet.</summary>
    Pending = 0,

    /// <summary>Orchestrator is actively processing.</summary>
    Running = 1,

    /// <summary>User cancelled (Esc / Shift+X / explicit cancel).</summary>
    Cancelled = 2,

    /// <summary>All episodes succeeded and feed is published + reachable.</summary>
    FullSuccess = 3,

    /// <summary>Some episodes failed but at least one published.</summary>
    PartialSuccess = 4,

    /// <summary>All episodes failed or the publish step itself failed.</summary>
    TotalFailure = 5,

    /// <summary>
    /// Row was Running when a previous app instance exited. Marked on
    /// startup by PodcastJobLifecycleService (workspace-nk06).
    /// </summary>
    Interrupted = 6,
}
