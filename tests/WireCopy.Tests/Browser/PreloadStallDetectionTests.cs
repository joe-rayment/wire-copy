// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-fh7g — stall-detection highlight in the prefetch detail
/// overlay. Two-layer rules: past 8s the "Now:" URL renders warning-colored
/// with elapsed suffix; past 30s the "looks stuck — Shift+R to retry"
/// hint appears. Below 8s the panel stays calm.
/// </summary>
[Trait("Category", "Unit")]
public class PreloadStallDetectionTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    private static PreloadProgress MakeProgress(string? url, TimeSpan? elapsed)
    {
        return new PreloadProgress
        {
            TotalCacheableLinks = 10,
            CachedCount = 3,
            CurrentlyFetchingUrl = url,
            ElapsedOnCurrent = elapsed,
            CurrentStage = url == null ? PreloadStage.Idle : PreloadStage.Fetching,
            IsActivelyFetching = url != null,
        };
    }

    [Fact]
    public void FormatElapsed_SubSecond_RendersLessThanOneSecond()
    {
        PreloadDetailRenderer.FormatElapsed(TimeSpan.FromMilliseconds(200)).Should().Be("<1s");
    }

    [Fact]
    public void FormatElapsed_FewSeconds_RendersSecondsOnly()
    {
        PreloadDetailRenderer.FormatElapsed(TimeSpan.FromSeconds(12)).Should().Be("12s");
    }

    [Fact]
    public void FormatElapsed_MinutesAndSeconds_RendersBoth()
    {
        PreloadDetailRenderer.FormatElapsed(TimeSpan.FromSeconds(75)).Should().Be("1m 15s");
    }

    [Fact]
    public void FormatElapsed_ExactMinutes_OmitsSeconds()
    {
        PreloadDetailRenderer.FormatElapsed(TimeSpan.FromMinutes(2)).Should().Be("2m");
    }

    [Fact]
    public void BuildPanelLines_BelowWarningThreshold_NoStuckHint_NoElapsedSuffixOnNow()
    {
        // Below the 8s warning threshold the panel stays calm: the "Now:"
        // line has no elapsed suffix, and the "looks stuck" hint isn't
        // rendered. We also assert plain text doesn't contain "looks stuck"
        // so this can't pass by accident on a colour-only failure.
        var progress = MakeProgress("https://example.com/page", TimeSpan.FromSeconds(3));

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth: 100);
        var plain = string.Join('\n', lines.ConvertAll(l => l.PlainText));

        plain.Should().Contain("Now: https://example.com/page");
        plain.Should().NotContain("(3s)",
            because: "below the 8s threshold the elapsed suffix on Now is hidden — the panel stays quiet");
        plain.Should().NotContain("looks stuck",
            because: "the stuck hint only fires at the 30s threshold");
    }

    [Fact]
    public void BuildPanelLines_PastWarningThreshold_RendersElapsedSuffixOnNow()
    {
        // 10 seconds elapsed crosses the 8s warning line. The "Now:" line
        // gains "(10s)" so the user can see the stall at a glance.
        var progress = MakeProgress("https://example.com/page", TimeSpan.FromSeconds(10));

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth: 100);
        var plain = string.Join('\n', lines.ConvertAll(l => l.PlainText));

        plain.Should().Contain("(10s)",
            because: "past 8s the elapsed suffix surfaces on the Now line");
        plain.Should().NotContain("looks stuck",
            because: "10s is past the warning threshold but well below the 30s stuck threshold");
    }

    [Fact]
    public void BuildPanelLines_PastStuckThreshold_RendersStuckHint()
    {
        // 45 seconds — past the 30s stuck threshold. The hint line MUST
        // appear directly after the stage chip so the user knows how to
        // recover.
        var progress = MakeProgress("https://example.com/page", TimeSpan.FromSeconds(45));

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth: 100);
        var plain = string.Join('\n', lines.ConvertAll(l => l.PlainText));

        plain.Should().Contain("(45s)");
        plain.Should().Contain("looks stuck — Shift+R to retry",
            because: "past 30s the hint must appear so the user knows the recovery key");
    }

    [Fact]
    public void BuildPanelLines_NoCurrentUrl_DoesNotRenderStuckHint()
    {
        // Even if a stale ElapsedOnCurrent value were somehow set with no
        // CurrentlyFetchingUrl (defensive guard), the hint should not fire
        // because BuildNowLine renders "Now: idle" and stall semantics don't
        // apply to an idle preloader.
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 2,
            CurrentlyFetchingUrl = null,
            ElapsedOnCurrent = TimeSpan.FromSeconds(60),
            CurrentStage = PreloadStage.Idle,
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth: 100);
        var plain = string.Join('\n', lines.ConvertAll(l => l.PlainText));

        plain.Should().Contain("Now: idle");
        plain.Should().NotContain("looks stuck",
            because: "idle preloader never shows the stall recovery hint");
    }

    [Fact]
    public void StallThresholds_ExactValues_MatchAcceptanceCriteria()
    {
        // Pin the acceptance-criterion numbers so a future tweak (e.g., to
        // 5s/20s) can't accidentally drift past the bead's contract.
        PreloadDetailRenderer.StallWarningThreshold.Should().Be(TimeSpan.FromSeconds(8));
        PreloadDetailRenderer.StallStuckThreshold.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void BuildPanelLines_PastWarningThreshold_NowLineStyledTextCarriesWarningAnsi()
    {
        // QA-flagged: plain-text-only assertions would pass even if the
        // color were dropped. The whole point of this bead is the warning
        // color — assert the warning ANSI sequence is actually present on
        // the styled Now line past the 8s threshold.
        var progress = MakeProgress("https://example.com/page", TimeSpan.FromSeconds(12));

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth: 100);
        var nowLine = lines.Find(l => l.PlainText.StartsWith("Now: "));

        nowLine.StyledText.Should().Contain(
            Palette.GetWarningFg().AnsiFg,
            because: "past the 8s stall threshold the Now URL + elapsed suffix must render in warning color");
    }

    [Fact]
    public void BuildPanelLines_BelowWarningThreshold_NowLineStyledTextDoesNotCarryWarningAnsi()
    {
        // Counterpart to the above: confirm the calm state does NOT use the
        // warning color. Catches the regression where someone flips the
        // gate to "always paint warning".
        var progress = MakeProgress("https://example.com/page", TimeSpan.FromSeconds(3));

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth: 100);
        var nowLine = lines.Find(l => l.PlainText.StartsWith("Now: "));

        nowLine.StyledText.Should().NotContain(
            Palette.GetWarningFg().AnsiFg,
            because: "below the 8s threshold the panel must stay quiet");
    }

    [Fact]
    public void BuildPanelLines_StuckHintAtMinimumPanelWidth_FitsWithoutOverflow()
    {
        // QA-flagged: a long URL plus the (45s) suffix plus the stuck-hint
        // line must all fit inside the panel at the minimum supported width.
        // The hint string itself is ~31 chars (`"looks stuck — Shift+R to retry"`)
        // plus 6 leading spaces of indent — at MinPanelWidth=50 that's well
        // within 50 cols of inner width. Verify the plain text never exceeds
        // the inner width.
        var longUrl = "https://www.example.com/a/very-long-article-slug/that-should-truncate";
        var progress = MakeProgress(longUrl, TimeSpan.FromSeconds(45));
        var minTerminal = PreloadDetailRenderer.MinTerminalWidthForOverlay;
        var innerWidth = Math.Min(120, Math.Max(50, minTerminal - 8));

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, terminalWidth: minTerminal);

        var hintLine = lines.Find(l => l.PlainText.Contains("looks stuck"));
        hintLine.PlainText.Length.Should().BeLessThanOrEqualTo(
            innerWidth,
            because: $"the stuck-hint line must fit inside the inner panel width ({innerWidth}) at the minimum terminal size");

        var nowLine = lines.Find(l => l.PlainText.StartsWith("Now: "));
        nowLine.PlainText.Length.Should().BeLessThanOrEqualTo(
            innerWidth,
            because: $"the Now line including the elapsed suffix must fit within inner width ({innerWidth})");
    }
}

/// <summary>
/// workspace-fh7g — service-level verification that the
/// <see cref="BackgroundPreloadService"/> populates
/// <see cref="PreloadProgress.ElapsedOnCurrent"/> from its internal
/// _currentlyFetchingStartedAtTicks field, advances it across GetProgress
/// calls, and clears it when no fetch is in flight.
/// </summary>
[Trait("Category", "Unit")]
public class PreloadServiceElapsedOnCurrentTests : IDisposable
{
    private readonly BackgroundPreloadService _service;

    public PreloadServiceElapsedOnCurrentTests()
    {
        var cache = Substitute.For<IPageCache>();
        var idleDetector = Substitute.For<IIdleDetector>();
        var httpClient = new HttpClient();
        var config = new CacheConfiguration();
        _service = new BackgroundPreloadService(
            cache, idleDetector, httpClient, config,
            NullLogger<BackgroundPreloadService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    private BackgroundPreloadService MakeService() => _service;

    [Fact]
    public void ElapsedOnCurrent_NoActiveFetch_IsNull()
    {
        var service = MakeService();

        var progress = service.GetProgress();

        progress.ElapsedOnCurrent.Should().BeNull(
            "no in-flight URL means there's nothing to time");
        progress.CurrentlyFetchingUrl.Should().BeNull();
    }

    [Fact]
    public void ElapsedOnCurrent_DuringFetch_IsApproximatelyWallClockSinceStart()
    {
        var service = MakeService();
        var startedAt = DateTime.UtcNow.AddSeconds(-3);
        service.SetCurrentlyFetchingForTesting("https://example.com/a", startedAt);

        var progress = service.GetProgress();

        progress.ElapsedOnCurrent.Should().NotBeNull();
        progress.ElapsedOnCurrent!.Value.TotalSeconds.Should().BeGreaterThanOrEqualTo(2.9,
            because: "we primed startedAt to 3s ago");
        progress.ElapsedOnCurrent.Value.TotalSeconds.Should().BeLessThan(5,
            because: "the assertion should run comfortably within 2s of the prime");
        progress.CurrentlyFetchingUrl.Should().Be("https://example.com/a");
    }

    [Fact]
    public void ElapsedOnCurrent_AfterFetchEnds_ResetsToNull()
    {
        // Simulate a fetch completing: clear both fields together. The next
        // GetProgress must report no in-flight URL and no elapsed time.
        var service = MakeService();
        service.SetCurrentlyFetchingForTesting("https://example.com/a", DateTime.UtcNow.AddSeconds(-2));
        service.SetCurrentlyFetchingForTesting(null, null);

        var progress = service.GetProgress();

        progress.CurrentlyFetchingUrl.Should().BeNull();
        progress.ElapsedOnCurrent.Should().BeNull(
            "fetch ended → both the URL and the timestamp must clear together");
    }
}
