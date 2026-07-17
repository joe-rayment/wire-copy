// Licensed under the MIT License. See LICENSE in the repository root.

using System;
using System.Text.Json;
using FluentAssertions;
using WireCopy.API;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-ua0c — the run-recipe verb's RUN_RESULT reports the EXACT run-now row it
/// created, never a re-queried "latest finished run" (which a concurrent scheduler
/// Skipped row could win). This covers the verb's reporting seam
/// (<see cref="Program.BuildRunResultJson"/>); <c>ScheduleRunNowTests</c> proves
/// <c>RunAsync</c> returns the run-now row under that exact adversarial ordering.
/// </summary>
[Trait("Category", "Unit")]
public class RunRecipeReportingTests
{
    private static ScheduledRun RunNowRow(ScheduledRunStatus status, int items,
        string? feed = null, string? local = null, string? errorMessage = null)
    {
        var run = ScheduledRun.Start(Guid.NewGuid(), "Brief", "run-now@2026-07-17T09:00:00#1");
        run.Finish(status, itemCount: items, targetLocalPath: local, targetFeedUrl: feed, errorMessage: errorMessage);
        return run;
    }

    [Fact]
    public void ReportsTheRunNowRowsTerminalOutcome_WithItsArtifacts()
    {
        var run = RunNowRow(ScheduledRunStatus.Completed, items: 3, feed: "https://feed/x.xml", local: "/tmp/x.m4b");

        var json = JsonDocument.Parse(Program.BuildRunResultJson(new RunNowResult(RunNowOutcome.Started, run))).RootElement;

        json.GetProperty("status").GetString().Should().Be("Completed", "the verb reports the run-now row's own status, never a Skipped tick row");
        json.GetProperty("feedUrl").GetString().Should().Be("https://feed/x.xml");
        json.GetProperty("localPath").GetString().Should().Be("/tmp/x.m4b");
        json.GetProperty("itemCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public void ReportsFailedRunNowOutcome_WithItsError()
    {
        var run = RunNowRow(ScheduledRunStatus.Failed, items: 0, errorMessage: "A required section contributed no articles this occurrence");

        var json = JsonDocument.Parse(Program.BuildRunResultJson(new RunNowResult(RunNowOutcome.Started, run))).RootElement;

        // A real run-now FAILURE is still the run-now outcome — never the "Skipped"
        // the scheduler tick would have written for a past-grace slot.
        json.GetProperty("status").GetString().Should().Be("Failed");
        json.GetProperty("error").GetString().Should().Be("A required section contributed no articles this occurrence");
    }

    [Fact]
    public void ReportsBusy_WhenGateHeld_AndNoRunExists()
    {
        var json = JsonDocument.Parse(Program.BuildRunResultJson(new RunNowResult(RunNowOutcome.Busy, null))).RootElement;

        json.GetProperty("status").GetString().Should().Be("Busy");
        json.GetProperty("itemCount").GetInt32().Should().Be(0);
        json.GetProperty("feedUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
