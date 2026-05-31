// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Scheduling;

/// <summary>
/// workspace-frpl.2 — when a recipe should run: a set of local days-of-week and
/// a local time-of-day, with an optional grace window for catching a slot the
/// app was closed for (B3 missed-run policy). Times are LOCAL; the
/// <c>NextDueCalculator</c> (B3) owns the clock/DST handling.
/// </summary>
public sealed record Cadence
{
    public required IReadOnlySet<DayOfWeek> Days { get; init; }

    public required TimeOnly LocalTime { get; init; }

    /// <summary>
    /// How long after the scheduled time a missed slot may still fire (e.g. the
    /// app opened late). Null = run only if currently within the same minute.
    /// </summary>
    public TimeSpan? GraceWindow { get; init; }

    public static Cadence Create(IEnumerable<DayOfWeek> days, TimeOnly localTime, TimeSpan? graceWindow = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        var set = days.ToHashSet();
        if (set.Count == 0)
        {
            throw new ArgumentException("A cadence must specify at least one day", nameof(days));
        }

        if (graceWindow is { } g && g < TimeSpan.Zero)
        {
            throw new ArgumentException("Grace window cannot be negative", nameof(graceWindow));
        }

        return new Cadence { Days = set, LocalTime = localTime, GraceWindow = graceWindow };
    }

    /// <summary>Convenience: every day at the given local time.</summary>
    public static Cadence Daily(TimeOnly localTime, TimeSpan? graceWindow = null) =>
        Create(Enum.GetValues<DayOfWeek>(), localTime, graceWindow);
}
