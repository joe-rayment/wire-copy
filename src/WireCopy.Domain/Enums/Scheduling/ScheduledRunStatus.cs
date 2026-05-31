// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Scheduling;

/// <summary>
/// Lifecycle state of one scheduled recipe run in the ScheduledRuns table.
/// Stored as int by EF Core; member ORDERING is append-only so migrations stay
/// backwards-compatible (mirrors PodcastJobStatus).
/// </summary>
public enum ScheduledRunStatus
{
    /// <summary>Row written, run not started.</summary>
    Pending = 0,

    /// <summary>Actively assembling/generating. The dedup marker for an occurrence.</summary>
    Running = 1,

    /// <summary>All steps resolved and the episode published.</summary>
    Completed = 2,

    /// <summary>Some steps contributed, some were skipped/blocked, but an episode published.</summary>
    PartialSuccess = 3,

    /// <summary>No content resolved or the publish step failed — no usable episode.</summary>
    Failed = 4,

    /// <summary>The occurrence was deliberately skipped (e.g. a required section did not match).</summary>
    Skipped = 5,

    /// <summary>Was Running when a previous app instance exited (startup orphan sweep).</summary>
    Interrupted = 6,
}
