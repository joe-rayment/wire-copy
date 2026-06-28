// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Browser.UI.StatusLine;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-wef6.6 — composer coverage for the new status bar: every
/// ViewMode at widths {45, 60, 80, 120} stays within budget with a loaded
/// right side; the HITL alert is visible at narrow widths; the help
/// affordance survives everywhere.
/// </summary>
[Trait("Category", "Unit")]
public class StatusLineCoverageTests
{
    public static readonly TheoryData<ViewMode, int> ModesAndWidths = BuildModesAndWidths();

    private static TheoryData<ViewMode, int> BuildModesAndWidths()
    {
        var data = new TheoryData<ViewMode, int>();
        foreach (var mode in new[] { ViewMode.Hierarchical, ViewMode.Readable, ViewMode.CollectionList, ViewMode.CollectionItems })
        {
            foreach (var width in new[] { 45, 60, 80, 120 })
            {
                data.Add(mode, width);
            }
        }

        return data;
    }

    private static NavigationContext LoadedContext(ViewMode mode) => new()
    {
        ViewMode = mode,
        BackHistoryCount = 2,
        SearchQuery = "climate",
        IsSpeedReadActive = mode == ViewMode.Readable,
        SpeedReadWpm = 450,
        ActiveAnnouncement = new StatusAnnouncement
        {
            Glyph = "✓",
            Text = "Saved (3)",
            Keys = new[] { new StatusKeyHint("c", "list") },
            ShortText = "✓3",
        },
    };

    private static PreloadProgress BusyProgress() => new()
    {
        TotalCacheableLinks = 12,
        CachedCount = 5,
        IsActivelyFetching = true,
        CurrentlyFetchingUrl = "https://example.com/article",
    };

    [Theory]
    [MemberData(nameof(ModesAndWidths))]
    public void LoadedBar_NeverExceedsBudget(ViewMode mode, int width)
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            LoadedContext(mode),
            mode,
            width,
            BusyProgress(),
            cacheUsagePercent: 92,
            readerTotalLines: mode == ViewMode.Readable ? 200 : 0,
            readerViewportHeight: 24,
            requiredAction: new HumanActionRequired(HumanActionVariant.Login, "nytimes.com"),
            browserDocked: true);

        RenderHelpers.GetDisplayWidth(model.PlainText).Should().BeLessThanOrEqualTo(width - 1,
            $"a fully loaded bar must fit at {mode}/{width}");
    }

    [Theory]
    [MemberData(nameof(ModesAndWidths))]
    public void LoadedBar_AlertAlwaysVisible(ViewMode mode, int width)
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            LoadedContext(mode),
            mode,
            width,
            BusyProgress(),
            cacheUsagePercent: 92,
            readerTotalLines: mode == ViewMode.Readable ? 200 : 0,
            readerViewportHeight: 24,
            requiredAction: new HumanActionRequired(HumanActionVariant.Login, "nytimes.com"),
            browserDocked: true);

        model.PlainText.Should().Contain("⏸",
            $"the HITL alert must survive at {mode}/{width} — epic acceptance pins narrow-width visibility");
        model.PlainText.Should().Contain("login",
            $"the alert keeps naming its verb at {mode}/{width}");
    }

    [Theory]
    [MemberData(nameof(ModesAndWidths))]
    public void LoadedBar_ActiveTransientNeverSilentlyDropped(ViewMode mode, int width)
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            LoadedContext(mode),
            mode,
            width,
            BusyProgress(),
            requiredAction: new HumanActionRequired(HumanActionVariant.Login, "nytimes.com"),
            browserDocked: true);

        model.PlainText.Should().Contain("✓",
            $"the active '✓ Saved' transient must be visible at {mode}/{width}");
    }

    [Fact]
    public void HitlAlert_At45Cols_ShowsDegradedCopyWithKey()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var model = StatusBarRenderer.ComposeStatusLine(
            context,
            ViewMode.Hierarchical,
            45,
            requiredAction: new HumanActionRequired(HumanActionVariant.Captcha, "nytimes.com"));

        model.PlainText.Should().Contain("⏸ captcha");
        model.PlainText.Should().Contain("|", "the recovery key survives degradation");
        RenderHelpers.GetDisplayWidth(model.PlainText).Should().BeLessThanOrEqualTo(44);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void HelpAffordance_SurvivesAllWidths(int width)
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            new NavigationContext { ViewMode = ViewMode.Hierarchical },
            ViewMode.Hierarchical,
            width);

        model.PlainText.Should().Contain("?", $"the help affordance must survive at width {width}");
    }
}
