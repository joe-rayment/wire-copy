// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>workspace-frpl.4 — what the scheduler should do with a recipe right now.</summary>
public enum SchedulingDecisionKind
{
    /// <summary>Fire the occurrence now.</summary>
    DueNow,

    /// <summary>Nothing to do; the next slot is <see cref="SchedulingDecision.NextAt"/>.</summary>
    NotDue,

    /// <summary>This occurrence already ran (dedup) — skip.</summary>
    AlreadyRanThisOccurrence,

    /// <summary>Today's slot was missed and is past its grace window — skip (record as missed).</summary>
    MissedPastGrace,
}

/// <summary>
/// workspace-frpl.4 — PURE cadence decision (no clock field, no I/O). Given the
/// cadence, the recipe's last-run state, and "now" (local), decides whether
/// TODAY'S occurrence is due, already ran, missed past grace, or not yet due.
/// It is intentionally TODAY-FOCUSED: the scheduler ticks frequently while the
/// app is open, so the only catch-up that matters is "today's slot passed while
/// the app was closed this morning" — within the grace window. It never replays
/// a backlog of earlier days. Dedup is keyed on the slot's wall-clock occurrence
/// key, so a slot straddling a DST change cannot double-fire.
/// </summary>
public static class NextDueCalculator
{
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(1);

    public static SchedulingDecision Decide(Cadence cadence, RecipeRunState state, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        ArgumentNullException.ThrowIfNull(state);

        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var nextSlot = NextSlotAfter(cadence, today, nowLocal);

        if (!cadence.Days.Contains(today.DayOfWeek))
        {
            return new SchedulingDecision { Kind = SchedulingDecisionKind.NotDue, NextAt = nextSlot };
        }

        var slot = SlotAt(today, cadence.LocalTime, nowLocal.Offset);
        if (nowLocal < slot)
        {
            // Today's slot hasn't arrived yet.
            return new SchedulingDecision { Kind = SchedulingDecisionKind.NotDue, NextAt = slot };
        }

        var key = OccurrenceKey(slot);
        if (string.Equals(state.LastRunOccurrenceKey, key, StringComparison.Ordinal))
        {
            return new SchedulingDecision { Kind = SchedulingDecisionKind.AlreadyRanThisOccurrence, OccurrenceKey = key, NextAt = nextSlot };
        }

        var grace = cadence.GraceWindow ?? DefaultGrace;
        return nowLocal - slot <= grace
            ? new SchedulingDecision { Kind = SchedulingDecisionKind.DueNow, OccurrenceKey = key, NextAt = nextSlot }
            : new SchedulingDecision { Kind = SchedulingDecisionKind.MissedPastGrace, OccurrenceKey = key, NextAt = nextSlot };
    }

    /// <summary>The stable per-occurrence key — wall-clock date + time of the slot.</summary>
    public static string OccurrenceKey(DateTimeOffset slot) => slot.ToString("yyyy-MM-dd@HH:mm");

    private static DateTimeOffset? NextSlotAfter(Cadence cadence, DateOnly today, DateTimeOffset nowLocal)
    {
        for (var d = 0; d <= 7; d++)
        {
            var date = today.AddDays(d);
            if (!cadence.Days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var slot = SlotAt(date, cadence.LocalTime, nowLocal.Offset);
            if (slot > nowLocal)
            {
                return slot;
            }
        }

        return null;
    }

    private static DateTimeOffset SlotAt(DateOnly date, TimeOnly time, TimeSpan offset) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0, offset);
}
