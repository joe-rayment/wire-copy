// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>workspace-frpl.4 — the <see cref="NextDueCalculator"/>'s verdict for one recipe at one instant.</summary>
public sealed record SchedulingDecision
{
    public required SchedulingDecisionKind Kind { get; init; }

    /// <summary>The slot key (yyyy-MM-dd@HH:mm) for DueNow / MissedPastGrace / AlreadyRan.</summary>
    public string? OccurrenceKey { get; init; }

    /// <summary>The next future slot (for NotDue, and surfaced on other outcomes for diagnostics).</summary>
    public DateTimeOffset? NextAt { get; init; }
}
