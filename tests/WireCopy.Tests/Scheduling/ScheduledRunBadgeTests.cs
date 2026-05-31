// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.13 (B11) — the launcher-badge formatter: failures/recoveries
/// surface (never a clean success), many collapse to a count, and a recovered run is
/// distinguished from a hard failure.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduledRunBadgeTests
{
    private static ScheduledRun Finished(string name, ScheduledRunStatus status, string? error = null, string? stepJson = null)
    {
        var run = ScheduledRun.Start(Guid.NewGuid(), name, $"2026-06-01@07:00#{Guid.NewGuid():N}");
        run.Finish(status, itemCount: status == ScheduledRunStatus.Failed ? 0 : 3, stepOutcomesJson: stepJson, errorClass: error == null ? null : "X", errorMessage: error);
        return run;
    }

    [Fact]
    public void NoRuns_OrOnlyCleanSuccess_NoBadge()
    {
        ScheduledRunBadge.Describe(Array.Empty<ScheduledRun>()).Should().BeNull();
        ScheduledRunBadge.Describe(new[] { Finished("A", ScheduledRunStatus.Completed) })
            .Should().BeNull("a clean success never nags");
    }

    [Fact]
    public void SingleFailure_NamesTheRecipe_AndReason()
    {
        var badge = ScheduledRunBadge.Describe(new[] { Finished("NYT Brief", ScheduledRunStatus.Failed, error: "A required section contributed no articles") });
        badge.Should().NotBeNull();
        badge.Should().Contain("NYT Brief").And.Contain("failed").And.Contain("required section");
        badge.Should().Contain(":schedules");
    }

    [Fact]
    public void MultipleAttentionRuns_CollapseToCount()
    {
        var badge = ScheduledRunBadge.Describe(new[]
        {
            Finished("A", ScheduledRunStatus.Failed),
            Finished("B", ScheduledRunStatus.Interrupted),
            Finished("C", ScheduledRunStatus.PartialSuccess),
        });
        badge.Should().Be("⚠ 3 scheduled runs need attention — open :schedules");
    }

    [Fact]
    public void RecoveredRun_IsDistinctFromHardFailure()
    {
        var recovered = ScheduledRunBadge.Describe(new[]
        {
            Finished("NPR Brief", ScheduledRunStatus.Completed, stepJson: """[{"Status":"Recovered","SectionName":"Business"}]"""),
        });
        recovered.Should().NotBeNull();
        recovered.Should().Contain("NPR Brief").And.Contain("ratify");
        recovered.Should().NotContain("failed");
    }

    [Fact]
    public void CleanSuccessMixedWithFailure_BadgesOnlyTheFailure()
    {
        var badge = ScheduledRunBadge.Describe(new[]
        {
            Finished("Clean", ScheduledRunStatus.Completed),
            Finished("Broken", ScheduledRunStatus.Failed, error: "boom"),
        });
        badge.Should().Contain("Broken").And.Contain("failed");
        badge.Should().NotContain("Clean");
    }
}
