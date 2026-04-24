// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI;
using TermReader.Infrastructure.Browser.UI.Components;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Snapshot-style tests that verify structural properties of rendered screens
/// across all themes. Instead of brittle golden-file comparison, these tests
/// assert on ANSI color codes, Unicode characters, line counts, mode labels,
/// and key hint styling for each theme x screen combination.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class ScreenRenderingSnapshotTests
{
    private const string AnsiReset = "\x1b[0m";

    public static IEnumerable<object[]> AllThemes =>
        Enum.GetValues<ThemeName>().Select(t => new object[] { t });

    public static IEnumerable<object[]> AllThemesAndViewModes =>
        from theme in Enum.GetValues<ThemeName>()
        from mode in Enum.GetValues<ViewMode>()
        select new object[] { theme, mode };

    public static IEnumerable<object[]> AllThemesAndWidths =>
        from theme in Enum.GetValues<ThemeName>()
        from width in new[] { 80, 120 }
        select new object[] { theme, width };

    private static string CaptureConsoleOutput(Action action)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static StatusBarRenderer CreateStatusBarRenderer(ThemeName theme)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(theme);
        var helpers = new RenderHelpers();
        return new StatusBarRenderer(helpers, themeProvider);
    }

    private static TerminalPageRenderer CreatePageRenderer(ThemeName theme)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(theme);
        var logger = Substitute.For<ILogger<TerminalPageRenderer>>();
        return new TerminalPageRenderer(themeProvider, logger);
    }

    private static NavigationContext CreateContext(ViewMode mode = ViewMode.Hierarchical)
    {
        return new NavigationContext { ViewMode = mode };
    }

    #region StatusBar - Theme Color Presence (5 ViewModes x 4 Themes = 20 tests)

    [Theory]
    [MemberData(nameof(AllThemesAndViewModes))]
    public void StatusBar_ContainsThemeStatusBarTextColor(ThemeName theme, ViewMode mode)
    {
        var p = BuiltInThemes.Get(theme);
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(mode);

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, mode, 120));

        // The status bar should contain the theme's StatusBarTextFg ANSI code
        // (used in the mode badge)
        output.Should().Contain(p.StatusBarTextFg.AnsiFg,
            $"StatusBar for {theme}/{mode} should use StatusBarTextFg");
    }

    [Theory]
    [MemberData(nameof(AllThemesAndViewModes))]
    public void StatusBar_ContainsDimmedSeparatorRule(ThemeName theme, ViewMode mode)
    {
        var p = BuiltInThemes.Get(theme);
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(mode);

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, mode, 120));

        // The dimmed rule uses DimFg with dim attribute (\x1b[2m)
        output.Should().Contain(p.GetDimFg().AnsiFg,
            $"StatusBar for {theme}/{mode} should contain DimFg in separator rule");
        output.Should().Contain("\u2500",
            $"StatusBar for {theme}/{mode} should contain box-drawing horizontal line");
    }

    [Theory]
    [MemberData(nameof(AllThemesAndViewModes))]
    public void StatusBar_ContainsPascalCaseModeLabel(ThemeName theme, ViewMode mode)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(mode);

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, mode, 120));

        var expectedLabel = mode switch
        {
            ViewMode.Hierarchical => "LinkView",
            ViewMode.Readable => "ReaderView",
            ViewMode.CollectionList => "Collections",
            ViewMode.CollectionItems => "ReadingList",
            ViewMode.Launcher => "Launcher",
            _ => "Browser"
        };

        output.Should().Contain(expectedLabel,
            $"StatusBar for {theme}/{mode} should contain PascalCase mode label '{expectedLabel}'");
    }

    [Theory]
    [MemberData(nameof(AllThemesAndViewModes))]
    public void StatusBar_HelpHintUsesAccentFgColor(ThemeName theme, ViewMode mode)
    {
        var p = BuiltInThemes.Get(theme);
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(mode);

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, mode, 120));

        // The help hint "?" key uses AccentFg
        output.Should().Contain(p.GetAccentFg().AnsiFg,
            $"StatusBar for {theme}/{mode} should use AccentFg for key hints");
    }

    [Theory]
    [MemberData(nameof(AllThemesAndViewModes))]
    public void StatusBar_ContainsAnsiResetCode(ThemeName theme, ViewMode mode)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(mode);

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, mode, 120));

        output.Should().Contain(AnsiReset,
            $"StatusBar for {theme}/{mode} should contain ANSI reset codes");
    }

    [Theory]
    [MemberData(nameof(AllThemesAndViewModes))]
    public void StatusBar_ProducesNonEmptyOutput(ThemeName theme, ViewMode mode)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(mode);

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, mode, 120));

        output.Should().NotBeNullOrWhiteSpace(
            $"StatusBar for {theme}/{mode} should produce non-empty output");
    }

    #endregion

    #region StatusBar - Adaptive Hints Per Mode

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_Hierarchical_AdaptiveHintsContainExpectedKeys(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 120);

        // Full tier at 120 width should include save-all
        hints.Should().Contain("save-all",
            $"Hierarchical hints at full width should include save-all for {theme}");
        hints.Should().Contain("Enter",
            $"Hierarchical hints should include Enter key for {theme}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_Readable_AdaptiveHintsContainExpectedKeys(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Readable, p, 120);

        hints.Should().Contain("save",
            $"Readable hints should include save for {theme}");
        hints.Should().Contain("back",
            $"Readable hints should include back for {theme}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_CollectionList_AdaptiveHintsContainExpectedKeys(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.CollectionList, p, 120);

        hints.Should().Contain("Enter",
            $"CollectionList hints should include Enter for {theme}");
        hints.Should().Contain("back",
            $"CollectionList hints should include back for {theme}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_CollectionItems_AdaptiveHintsContainExpectedKeys(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.CollectionItems, p, 120);

        hints.Should().Contain("Enter",
            $"CollectionItems hints should include Enter for {theme}");
        hints.Should().Contain("podcast",
            $"CollectionItems hints should include podcast for {theme}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_Launcher_AdaptiveHintsContainExpectedKeys(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Launcher, p, 120);

        hints.Should().Contain("Enter",
            $"Launcher hints should include Enter for {theme}");
        hints.Should().Contain("go to url",
            $"Launcher hints should include go to url for {theme}");
    }

    #endregion

    #region StatusBar - Width Resilience (4 themes x 2 widths)

    [Theory]
    [MemberData(nameof(AllThemesAndWidths))]
    public void StatusBar_AtDifferentWidths_ProducesValidOutput(ThemeName theme, int width)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(ViewMode.Hierarchical);

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, width));

        output.Should().NotBeNullOrWhiteSpace(
            $"StatusBar at {width} cols for {theme} should produce non-empty output");
        output.Should().Contain("LinkView",
            $"StatusBar at {width} cols for {theme} should contain mode label");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_AtNarrowWidth_DoesNotCrash(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(ViewMode.Hierarchical);

        // Very narrow: 40 columns
        var act = () => CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 40));

        act.Should().NotThrow(
            $"StatusBar at 40 cols for {theme} should not throw");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_AtVeryWideWidth_DoesNotCrash(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = CreateContext(ViewMode.Hierarchical);

        // Very wide: 200 columns
        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 200));

        output.Should().NotBeNullOrWhiteSpace(
            $"StatusBar at 200 cols for {theme} should produce non-empty output");
    }

    #endregion

    #region StatusBar - Domain Display with Page Context

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_WithPage_ShowsDomain(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var page = Page.Create(
            "https://example.com/article",
            "<html></html>",
            new PageMetadata { Title = "Test" });
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            CurrentPage = page,
        };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().Contain("example.com",
            $"StatusBar for {theme} should display domain when page is set");
    }

    #endregion

    #region StatusBar - Back Arrow Indicator

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_WithBackHistory_ShowsBackArrow(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            BackHistoryCount = 3,
        };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().Contain("\u2190",
            $"StatusBar for {theme} should show left arrow when back history exists");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_WithoutBackHistory_NoBackArrow(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            BackHistoryCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().NotContain("\u2190",
            $"StatusBar for {theme} should not show left arrow when no back history");
    }

    #endregion

    #region StatusBar - Reader View Line Info

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_ReaderView_ShowsLineAndWidthInfo(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ScrollOffset = 9,
        };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Readable, 120,
                readerTotalLines: 100, readerContentWidth: 80, readerViewportHeight: 20));

        output.Should().Contain("L10/100",
            $"ReaderView StatusBar for {theme} should show line position");
        output.Should().Contain("W80",
            $"ReaderView StatusBar for {theme} should show content width");
    }

    #endregion

    #region Loading Screen - Theme Color Presence (4 themes)

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ContainsThemePrimaryTextColor(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com/page", "Loading..."));

        output.Should().Contain(p.PrimaryText.AnsiFg,
            $"Loading screen for {theme} should use PrimaryText color");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ContainsAccentFgForEscKey(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com/page"));

        output.Should().Contain(p.GetAccentFg().AnsiFg,
            $"Loading screen for {theme} should use AccentFg for Esc key hint");
        output.Should().Contain("Esc",
            $"Loading screen for {theme} should show Esc key hint");
        output.Should().Contain("cancel",
            $"Loading screen for {theme} should show cancel action");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ContainsLoadingLabel(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com"));

        output.Should().Contain("Loading",
            $"Loading screen for {theme} should contain 'Loading' text");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ContainsUrlDomain(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com/long/path"));

        output.Should().Contain("example.com",
            $"Loading screen for {theme} should show URL domain");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ContainsBoxDrawingCharacters(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com"));

        // Box-drawing: top-left corner, horizontal line, vertical bar
        output.Should().Contain("\u256d",
            $"Loading screen for {theme} should contain rounded top-left corner");
        output.Should().Contain("\u2500",
            $"Loading screen for {theme} should contain horizontal line");
        output.Should().Contain("\u2502",
            $"Loading screen for {theme} should contain vertical bar");
        output.Should().Contain("\u256f",
            $"Loading screen for {theme} should contain rounded bottom-right corner");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ContainsBrailleSpinner(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com", "Loading...", elapsedMs: 0));

        // The spinner uses braille pattern characters (U+2800 block)
        output.Should().MatchRegex("[\u2800-\u28FF]",
            $"Loading screen for {theme} should contain a braille spinner character");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ContainsSecondaryTextColor(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com/page"));

        output.Should().Contain(p.SecondaryText.AnsiFg,
            $"Loading screen for {theme} should use SecondaryText for URL");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_ProducesSubstantialOutput(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com"));

        // The loading screen renders a centered box with multiple visual elements.
        // Rather than counting lines (unreliable with cursor-positioned rendering),
        // verify the output has substantial content (box borders + text + padding).
        output.Length.Should().BeGreaterThan(50,
            $"Loading screen for {theme} should produce substantial output");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Loading_WithElapsedTime_ShowsSeconds(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderLoading("https://example.com", "Loading...", elapsedMs: 5000));

        output.Should().Contain("5s",
            $"Loading screen for {theme} should show elapsed seconds");
    }

    #endregion

    #region Error Screen - Theme Color Presence (4 themes)

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Error_ContainsErrorFgBorderColor(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderError("Connection refused", "https://example.com"));

        output.Should().Contain(p.ErrorFg.AnsiFg,
            $"Error screen for {theme} should use ErrorFg for border color");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Error_ContainsErrorMessage(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderError("Connection refused", "https://example.com"));

        output.Should().Contain("Connection refused",
            $"Error screen for {theme} should show the error message");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Error_ContainsSomethingWentWrongTitle(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderError("Timeout", "https://example.com"));

        output.Should().Contain("Something went wrong",
            $"Error screen for {theme} should show 'Something went wrong' title");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Error_ContainsAccentFgForKeyHints(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderError("Error", "https://example.com"));

        output.Should().Contain(p.GetAccentFg().AnsiFg,
            $"Error screen for {theme} should use AccentFg for key hints");
        output.Should().Contain("back",
            $"Error screen for {theme} should show back action");
        output.Should().Contain("retry",
            $"Error screen for {theme} should show retry action");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Error_ContainsBoxDrawingCharacters(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderError("Error", "https://example.com"));

        output.Should().Contain("\u256d",
            $"Error screen for {theme} should contain rounded top-left corner");
        output.Should().Contain("\u256f",
            $"Error screen for {theme} should contain rounded bottom-right corner");
        output.Should().Contain("\u2502",
            $"Error screen for {theme} should contain vertical bar");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Error_ContainsUrl(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderError("Error", "https://example.com/fail"));

        output.Should().Contain("example.com",
            $"Error screen for {theme} should show the URL");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Error_ProducesSubstantialOutput(ThemeName theme)
    {
        var renderer = CreatePageRenderer(theme);

        var output = CaptureConsoleOutput(() =>
            renderer.RenderError("Error", "https://example.com"));

        // The error screen renders a centered box with multiple visual elements.
        // Rather than counting lines (unreliable with cursor-positioned rendering),
        // verify the output has substantial content.
        output.Length.Should().BeGreaterThan(50,
            $"Error screen for {theme} should produce substantial output");
    }

    #endregion

    #region Cross-Theme Consistency - Mode Labels

    [Fact]
    public void GetModeLabel_ReturnsPascalCase_ForAllModes()
    {
        foreach (var mode in Enum.GetValues<ViewMode>())
        {
            var label = StatusBarRenderer.GetModeLabel(mode);

            label.Should().NotBeNullOrWhiteSpace($"Mode label for {mode} should not be empty");
            char.IsUpper(label[0]).Should().BeTrue(
                $"Mode label '{label}' for {mode} should start with uppercase");
            label.Should().NotContain(" ",
                $"Mode label '{label}' for {mode} should not contain spaces (PascalCase)");
        }
    }

    #endregion

    #region Cross-Theme Consistency - Each Theme Has Distinct Colors

    [Fact]
    public void AllThemes_HaveDistinctAccentFgCodes()
    {
        var codes = Enum.GetValues<ThemeName>()
            .Select(t => BuiltInThemes.Get(t).GetAccentFg().AnsiFg)
            .ToList();

        codes.Distinct().Count().Should().Be(codes.Count,
            "each theme should have a distinct AccentFg ANSI code");
    }

    [Fact]
    public void AllThemes_HaveDistinctStatusBarTextFgCodes()
    {
        var codes = Enum.GetValues<ThemeName>()
            .Select(t => BuiltInThemes.Get(t).StatusBarTextFg.AnsiFg)
            .ToList();

        codes.Distinct().Count().Should().Be(codes.Count,
            "each theme should have a distinct StatusBarTextFg ANSI code");
    }

    #endregion

    #region Component-Level - Borders.DimmedRule

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void DimmedRule_UsesThemeDimFg(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);

        var rule = Borders.DimmedRule(p, 80);

        rule.Should().Contain(p.GetDimFg().AnsiFg,
            $"DimmedRule for {theme} should use that theme's DimFg");
        rule.Should().Contain("\x1b[2m",
            $"DimmedRule for {theme} should include dim/faint attribute");
        rule.Should().Contain("\u2500",
            $"DimmedRule for {theme} should contain horizontal line chars");
    }

    [Theory]
    [MemberData(nameof(AllThemesAndWidths))]
    public void DimmedRule_HasCorrectWidth(ThemeName theme, int width)
    {
        var p = BuiltInThemes.Get(theme);

        var rule = Borders.DimmedRule(p, width);

        // Count the horizontal line characters
        var lineCharCount = rule.Count(c => c == '\u2500');
        lineCharCount.Should().Be(width,
            $"DimmedRule for {theme} at {width} cols should contain {width} horizontal line chars");
    }

    #endregion

    #region Component-Level - FormatProgressBar

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FormatProgressBar_ContainsCountLabel(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);

        var bar = StatusBarRenderer.FormatProgressBar(3, 10, p);

        bar.Should().Contain("3/10",
            $"Progress bar for {theme} should show count label");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FormatProgressBar_ActiveState_ContainsWarningFg(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);

        var bar = StatusBarRenderer.FormatProgressBar(3, 10, p, isActive: true,
            currentUrl: "https://example.com/article");

        bar.Should().Contain(p.GetWarningFg().AnsiFg,
            $"Active progress bar for {theme} should use WarningFg");
    }

    #endregion

    #region Component-Level - Adaptive Hints Tiering

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AdaptiveHints_DropGracefully_AtNarrowWidths(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);

        var wideHints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 120);
        var narrowHints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 40);
        var tinyHints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 3);

        // Wider should have more content than narrower
        RenderHelpers.GetDisplayWidth(wideHints).Should().BeGreaterThanOrEqualTo(
            RenderHelpers.GetDisplayWidth(narrowHints),
            $"Wider hints should be >= narrower hints for {theme}");

        // Very tiny should produce empty
        tinyHints.Should().BeEmpty(
            $"Hints at 3 cols for {theme} should be empty");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AdaptiveHints_AllModes_ProduceValidOutput_AtFullWidth(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);

        foreach (var mode in Enum.GetValues<ViewMode>())
        {
            var hints = StatusBarRenderer.GetAdaptiveHints(mode, p, 120);

            hints.Should().NotBeNullOrWhiteSpace(
                $"Hints for {theme}/{mode} at 120 cols should not be empty");
            hints.Should().Contain(p.GetAccentFg().AnsiFg,
                $"Hints for {theme}/{mode} should use AccentFg for key labels");
            hints.Should().Contain(AnsiReset,
                $"Hints for {theme}/{mode} should contain ANSI reset");
        }
    }

    #endregion

    #region StatusBar - Search Query Display

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_WithSearchQuery_DisplaysQuery(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            SearchQuery = "test-query",
        };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().Contain("test-query",
            $"StatusBar for {theme} should display search query");
    }

    #endregion

    #region StatusBar - Cache From-Cache Badge

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_ReaderFromCache_ShowsCacheAge(ThemeName theme)
    {
        var statusBar = CreateStatusBarRenderer(theme);
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            IsFromCache = true,
            CachedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Readable, 120));

        output.Should().Contain("10m ago",
            $"StatusBar for {theme} should show cache age when from cache");
    }

    #endregion

    #region StatusBar - Preview Mode Controls

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusBar_InPreviewMode_ShowsPreviewControls(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);
        var statusBar = CreateStatusBarRenderer(theme);
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            IsInPreviewMode = true,
            PreviewLabel = "Layout 1/3",
        };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().Contain("cycle",
            $"StatusBar preview mode for {theme} should show cycle action");
        output.Should().Contain("save",
            $"StatusBar preview mode for {theme} should show save action");
        output.Should().Contain("cancel",
            $"StatusBar preview mode for {theme} should show cancel action");
        output.Should().Contain("Layout 1/3",
            $"StatusBar preview mode for {theme} should show preview label");
    }

    #endregion
}
