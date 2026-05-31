// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Scheduling;

namespace WireCopy.Domain.ValueObjects.Scheduling;

/// <summary>
/// workspace-frpl.2 — the recipe's last-run summary for UX (B11) and a
/// CONVENIENCE dedup cache. The AUTHORITATIVE dedup record is the EF
/// ScheduledRun row (B10/B6) — <see cref="LastRunOccurrenceKey"/> here is only a
/// hint so the schedules screen can render without a DB hit.
/// </summary>
public sealed record RecipeRunState
{
    public DateOnly? LastRunLocalDate { get; init; }

    public string? LastRunOccurrenceKey { get; init; }

    public RunStatus LastStatus { get; init; } = RunStatus.Never;

    /// <summary>When the user dismissed the last-result badge (B11), else null.</summary>
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }

    public static RecipeRunState Initial => new();
}
