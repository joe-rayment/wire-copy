// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Scheduling;

/// <summary>
/// workspace-frpl.8 (B7) — the per-step record of one recipe occurrence, serialized
/// onto <c>ScheduledRun.StepOutcomesJson</c> so the schedules screen (B11) can show
/// exactly which sources contributed and which were blocked/empty — never a silent
/// near-empty episode. <see cref="Status"/> is one of Resolved/ZeroMatch/
/// SectionNotFound/Blocked/LoadFailed (the run-time superset of <c>ResolutionStatus</c>
/// plus the headless-load failures).
/// </summary>
public sealed record RunStepOutcome
{
    public required string SectionName { get; init; }

    public required string SourceUrl { get; init; }

    public required string Status { get; init; }

    public required bool Required { get; init; }

    /// <summary>How many links matched the section before TakeMode trimming.</summary>
    public int MatchCount { get; init; }

    /// <summary>How many items this step actually contributed to the assembled collection (post-TakeMode, post-dedup).</summary>
    public int ItemsContributed { get; init; }

    public string? Diagnostic { get; init; }
}
