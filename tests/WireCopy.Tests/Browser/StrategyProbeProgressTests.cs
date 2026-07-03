// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Components;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-kany — the strategy chooser's availability probe must keep the
/// overlay visibly alive: while <c>IsAvailableAsync</c> is pending, each pump
/// tick advances the row's spinner frame, updates its detail to an
/// elapsed-seconds label, and repaints — instead of freezing on the first
/// "probing…" frame for the whole multi-second probe.
/// </summary>
[Trait("Category", "Unit")]
public class StrategyProbeProgressTests
{
    private static StrategyChooserOverlay.Row NewRow() => new()
    {
        Id = "rss",
        DisplayName = "RSS Feed",
        Detail = "probing…",
        State = StrategyChooserOverlay.RowState.Probing,
        SpinnerFrame = 0,
    };

    [Fact]
    public async Task SlowProbe_TicksSpinnerAndElapsedLabel_ThenReturnsResult()
    {
        var row = NewRow();
        var renders = 0;
        var detailsSeen = new List<string?>();

        var result = await StrategyChooserHandler.ProbeWithProgressAsync(
            probe: async token =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), token);
                return new ScrapingStrategyAvailability { IsAvailable = true };
            },
            row,
            render: _ =>
            {
                renders++;
                detailsSeen.Add(row.Detail);
                return Task.CompletedTask;
            },
            probeBudget: TimeSpan.FromSeconds(5),
            tickInterval: TimeSpan.FromMilliseconds(50),
            onTimeout: null,
            CancellationToken.None);

        result.IsAvailable.Should().BeTrue("the probe's own result is returned unchanged");
        renders.Should().BeGreaterThan(1, "the pump repaints while the probe is pending");
        row.SpinnerFrame.Should().BeGreaterThan(1, "each tick advances the spinner animation");
        detailsSeen.Should().OnlyContain(d => d != null && d.StartsWith("probing… ", StringComparison.Ordinal),
            "the detail shows a live elapsed-seconds label, e.g. 'probing… 2s'");
        detailsSeen.Should().OnlyContain(d => System.Text.RegularExpressions.Regex.IsMatch(d!, @"^probing… \d+s$"));
    }

    [Fact]
    public async Task ProbeExceedingBudget_ReportsTimeout_AndInvokesTimeoutHook()
    {
        var row = NewRow();
        var timedOut = false;

        var result = await StrategyChooserHandler.ProbeWithProgressAsync(
            probe: async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return new ScrapingStrategyAvailability { IsAvailable = true };
            },
            row,
            render: _ => Task.CompletedTask,
            probeBudget: TimeSpan.FromMilliseconds(150),
            tickInterval: TimeSpan.FromMilliseconds(50),
            onTimeout: () => timedOut = true,
            CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
        result.ReasonWhenUnavailable.Should().Contain("timed out", "budget overruns keep the honest reason");
        timedOut.Should().BeTrue("the caller's logging hook fires exactly as before");
    }

    [Fact]
    public async Task CallerCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => StrategyChooserHandler.ProbeWithProgressAsync(
            probe: async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return new ScrapingStrategyAvailability { IsAvailable = true };
            },
            NewRow(),
            render: _ => Task.CompletedTask,
            probeBudget: TimeSpan.FromSeconds(30),
            tickInterval: TimeSpan.FromMilliseconds(25),
            onTimeout: null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancelling the chooser is not a probe timeout");
    }
}
