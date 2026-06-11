// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-v04i — delivery fixes for the prefetch detail panel. The panel,
/// its data, and the toggle key all existed but were undeliverable: the key
/// was swallowed during background loads, refreshes only happened in two view
/// modes, a wedged fetch never re-rendered (so stall states were unreachable),
/// and the binding was advertised nowhere. These tests pin the new behaviour.
/// </summary>
[Trait("Category", "Unit")]
public class PreloadPanelDeliveryTests
{
    // ---- A. Toggle honoured during active background load ----

    [Fact]
    public void TogglePreloadDetail_IsAllowedDuringBackgroundLoad()
    {
        BrowserOrchestrator.IsCommandAllowedDuringBackgroundLoad(CommandType.TogglePreloadDetail)
            .Should().BeTrue("pressing \\ while staring at a skeleton screen is the #1 'is this stalled?' moment");
    }

    [Theory]
    [InlineData(CommandType.Quit)]
    [InlineData(CommandType.GoBack)]
    [InlineData(CommandType.NoOp)]
    [InlineData(CommandType.TerminalResized)]
    [InlineData(CommandType.AnimationTick)]
    public void ExistingPassiveCommands_RemainAllowedDuringBackgroundLoad(CommandType type)
    {
        BrowserOrchestrator.IsCommandAllowedDuringBackgroundLoad(type).Should().BeTrue();
    }

    [Theory]
    [InlineData(CommandType.MoveDown)]
    [InlineData(CommandType.ActivateLink)]
    [InlineData(CommandType.Refresh)]
    public void ActiveCommands_StayGatedDuringBackgroundLoad(CommandType type)
    {
        BrowserOrchestrator.IsCommandAllowedDuringBackgroundLoad(type).Should().BeFalse();
    }

    // ---- B. Live updates wherever the panel is open ----

    [Theory]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.Launcher)]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.CollectionItems)]
    [InlineData(ViewMode.CollectionList)]
    public void ProgressRefresh_AlwaysRunsWhenPanelVisible(ViewMode viewMode)
    {
        BrowserOrchestrator.ShouldRefreshForProgress(panelVisible: true, viewMode)
            .Should().BeTrue("the panel overlays every view, so progress must repaint it everywhere");
    }

    [Theory]
    [InlineData(ViewMode.Hierarchical, true)]
    [InlineData(ViewMode.CollectionItems, true)]
    [InlineData(ViewMode.Readable, false)]
    [InlineData(ViewMode.Launcher, false)]
    [InlineData(ViewMode.CollectionList, false)]
    public void ProgressRefresh_WithoutPanel_KeepsLegacyViewGate(ViewMode viewMode, bool expected)
    {
        BrowserOrchestrator.ShouldRefreshForProgress(panelVisible: false, viewMode)
            .Should().Be(expected);
    }

    [Fact]
    public void Heartbeat_FiresWhileFetchInFlight()
    {
        var progress = new PreloadProgress
        {
            CurrentlyFetchingUrl = "https://example.com/slow",
            ElapsedOnCurrent = TimeSpan.FromSeconds(12),
        };

        BrowserOrchestrator.ShouldHeartbeatRefresh(progress).Should().BeTrue(
            "a wedged fetch emits no ProgressChanged events; only the heartbeat can surface the stall states");
    }

    [Fact]
    public void Heartbeat_FiresWhileQueueNonEmpty()
    {
        var progress = new PreloadProgress
        {
            UpcomingUrls = new[] { "https://example.com/next" },
        };

        BrowserOrchestrator.ShouldHeartbeatRefresh(progress).Should().BeTrue();
    }

    [Fact]
    public void Heartbeat_SkippedWhenPreloaderFullyIdle()
    {
        var progress = new PreloadProgress();

        BrowserOrchestrator.ShouldHeartbeatRefresh(progress).Should().BeFalse(
            "an open panel over an idle preloader must not burn renders");
    }

    // ---- C. Discoverability ----

    [Fact]
    public void HelpText_AdvertisesPrefetchDetailKey()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var handler = new TerminalInputHandler(
            themeProvider,
            Substitute.For<IResizeDetector>(),
            Substitute.For<INavigationService>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<TerminalInputHandler>>());

        handler.GetHelpText().Should().Contain("Prefetch detail",
            "the user could not have found the \\ key — it was advertised nowhere");
    }

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.CollectionItems)]
    public void KeybindingPopup_AdvertisesBackslash(ViewMode mode)
    {
        KeybindingPopup.GetBindings(mode).Should().Contain(b => b.Key == "\\",
            $"the prefetch panel binding must be discoverable from the ? popup in {mode}");
    }

    // ---- D. Skip/failure reasons surfaced in Recent ----

    [Fact]
    public void RecentHistory_ShowsSkipReason()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 2,
            RecentItems = new[]
            {
                new PreloadHistoryEntry
                {
                    Url = "https://example.com/article",
                    Outcome = PreloadOutcome.Skipped,
                    ElapsedMs = 412,
                    Reason = "paywall",
                },
            },
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, palette, terminalWidth: 100);

        lines.Should().Contain(l => l.PlainText.Contains("— paywall"),
            "the user asked WHY entries are skipped; the reason renders dim after the URL");
    }

    [Fact]
    public void RecentHistory_ReasonRow_NeverExceedsInnerWidth()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
        const int terminalWidth = 60;
        var innerWidth = Math.Min(120, Math.Max(50, terminalWidth - 8));
        var progress = new PreloadProgress
        {
            RecentItems = new[]
            {
                new PreloadHistoryEntry
                {
                    Url = "https://example.com/some/very/long/path/that/will/be/truncated/article-slug",
                    Outcome = PreloadOutcome.Failed,
                    ElapsedMs = 15000,
                    Reason = "a fairly long failure reason that cannot possibly fit in the row",
                },
            },
        };

        var lines = PreloadDetailRenderer.BuildPanelLines(progress, palette, terminalWidth);

        foreach (var line in lines)
        {
            RenderHelpers.GetDisplayWidth(line.PlainText).Should().BeLessThanOrEqualTo(innerWidth,
                "no panel row may overflow the box border");
        }
    }
}
