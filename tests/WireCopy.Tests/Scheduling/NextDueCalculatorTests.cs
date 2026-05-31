// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

[Trait("Category", "Unit")]
public class NextDueCalculatorTests
{
    private static readonly TimeSpan Utc = TimeSpan.Zero;

    private static Cadence Weekdays(int hour = 7, TimeSpan? grace = null) => Cadence.Create(
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        new TimeOnly(hour, 0), grace);

    private static RecipeRunState Ran(string key) => RecipeRunState.Initial with { LastRunOccurrenceKey = key };

    // 2026-06-01 is a Monday; 2026-06-06 is a Saturday; 2026-05-31 is a Sunday.
    private static DateTimeOffset At(int y, int m, int d, int hh, int mm, TimeSpan? off = null) =>
        new(y, m, d, hh, mm, 0, off ?? Utc);

    [Fact]
    public void FiresOnAllowedDay_AtOrAfterSlot()
    {
        var d = NextDueCalculator.Decide(Weekdays(), RecipeRunState.Initial, At(2026, 6, 1, 7, 0)); // Mon 07:00
        d.Kind.Should().Be(SchedulingDecisionKind.DueNow);
        d.OccurrenceKey.Should().Be("2026-06-01@07:00");
    }

    [Fact]
    public void NotDue_OnDisallowedDay_ReturnsNextSlot()
    {
        var d = NextDueCalculator.Decide(Weekdays(), RecipeRunState.Initial, At(2026, 6, 6, 9, 0)); // Saturday
        d.Kind.Should().Be(SchedulingDecisionKind.NotDue);
        d.NextAt!.Value.Date.Should().Be(new DateTime(2026, 6, 8)); // next Monday
    }

    [Fact]
    public void NotDue_BeforeTodaysSlot()
    {
        var d = NextDueCalculator.Decide(Weekdays(), RecipeRunState.Initial, At(2026, 6, 1, 6, 30)); // Mon, before 07:00
        d.Kind.Should().Be(SchedulingDecisionKind.NotDue);
        d.NextAt.Should().Be(At(2026, 6, 1, 7, 0));
    }

    [Fact]
    public void SameOccurrence_Dedups()
    {
        var d = NextDueCalculator.Decide(Weekdays(), Ran("2026-06-01@07:00"), At(2026, 6, 1, 7, 0, null));
        d.Kind.Should().Be(SchedulingDecisionKind.AlreadyRanThisOccurrence);
    }

    [Fact]
    public void MissedWithinGrace_FiresExactlyOnce_NotPerMissedDay()
    {
        // App opens Wed 07:30 with a 2h grace; last run was Monday. Only the
        // most-recent occurrence (Wed) is caught up — Tue is NOT replayed.
        var d = NextDueCalculator.Decide(
            Weekdays(grace: TimeSpan.FromHours(2)),
            Ran("2026-06-01@07:00"),
            At(2026, 6, 3, 7, 30)); // Wednesday 07:30
        d.Kind.Should().Be(SchedulingDecisionKind.DueNow);
        d.OccurrenceKey.Should().Be("2026-06-03@07:00", "only Wednesday's slot, never a Tue backlog");
    }

    [Fact]
    public void PastGrace_YieldsMissed()
    {
        var d = NextDueCalculator.Decide(
            Weekdays(grace: TimeSpan.FromMinutes(30)),
            RecipeRunState.Initial,
            At(2026, 6, 1, 9, 0)); // Mon, 2h after the 07:00 slot, grace 30m
        d.Kind.Should().Be(SchedulingDecisionKind.MissedPastGrace);
        d.OccurrenceKey.Should().Be("2026-06-01@07:00");
    }

    [Fact]
    public void DstStraddle_DoesNotDoubleFire()
    {
        // Same wall-clock slot evaluated under two different UTC offsets (a DST
        // change). The occurrence key is wall-clock, so once it has run it
        // dedups regardless of the offset shift — no double-fire.
        var cadence = Weekdays(grace: TimeSpan.FromHours(6));
        var first = NextDueCalculator.Decide(cadence, RecipeRunState.Initial, At(2026, 6, 1, 7, 0, TimeSpan.FromHours(-5)));
        first.Kind.Should().Be(SchedulingDecisionKind.DueNow);

        var afterRun = Ran(first.OccurrenceKey!);
        // Clocks "fall back": same date + wall time, different offset.
        var second = NextDueCalculator.Decide(cadence, afterRun, At(2026, 6, 1, 7, 0, TimeSpan.FromHours(-4)));
        second.Kind.Should().Be(SchedulingDecisionKind.AlreadyRanThisOccurrence);
    }
}
