// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Verifies that the Phosphor 2.0 design system ANSI codes appear
/// in rendered output. Guards against colour regressions where old
/// neon-green, hardcoded-white, or wrong palette roles leak back in.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class ThemeRenderingTests
{
    private readonly IThemeProvider _themeProvider;
    private readonly RenderHelpers _helpers;
    private readonly StatusBarRenderer _statusBar;

    public ThemeRenderingTests()
    {
        _themeProvider = Substitute.For<IThemeProvider>();
        _themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _helpers = new RenderHelpers();
        _statusBar = new StatusBarRenderer(_helpers, _themeProvider);
    }

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

    #region Test 1: Phosphor palette has correct ANSI codes

    [Fact]
    public void PhosphorPalette_HasCorrectAnsiCodes()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);

        p.PrimaryText.AnsiFg.Should().Be("\x1b[38;5;40m", "PrimaryText should be phosphor green (40)");
        p.HeaderTitleFg.AnsiFg.Should().Be("\x1b[38;5;212m", "HeaderTitleFg should be pink (212)");
        p.GetAccentFg().AnsiFg.Should().Be("\x1b[38;5;51m", "AccentFg should be cyan (51)");
        p.GetDimFg().AnsiFg.Should().Be("\x1b[38;5;22m", "DimFg should be dim green (22)");
        p.GetWarningFg().AnsiFg.Should().Be("\x1b[38;5;214m", "WarningFg should be amber (214)");
        p.GetSuccessFg().AnsiFg.Should().Be("\x1b[38;5;119m", "SuccessFg should be success green (119)");
        p.GetCelebrationFg().AnsiFg.Should().Be("\x1b[38;5;206m", "CelebrationFg should be hot pink (206)");
        p.GetMutedFg().AnsiFg.Should().Be("\x1b[38;5;65m", "MutedFg should be muted green (65)");
        p.SearchHighlightBg.AnsiBg.Should().Be("\x1b[48;5;24m", "SearchHighlightBg should be blue-teal (24)");
    }

    #endregion

    #region Test 2: FormatHints uses AccentFg (ANSI 51) for keys

    [Fact]
    public void FormatHints_UsesAccentFg_ForKeyLabels()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);

        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 120);

        // AccentFg (cyan 51) should colour the key labels
        hints.Should().Contain("\x1b[38;5;51m", "key labels should use AccentFg cyan (51)");
    }

    #endregion

    #region Test 3: FormatHints does NOT contain old green (ANSI 46) for key labels

    [Fact]
    public void FormatHints_DoesNotContainOldNeonGreen()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);

        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 120);

        hints.Should().NotContain("\x1b[38;5;46m",
            "old neon green (46) must not appear in hint key labels");
    }

    #endregion

    #region Test 4: FormatProgressBar uses theme WarningFg (ANSI 214), not hardcoded 220

    [Fact]
    public void FormatProgressBar_UsesThemeWarningFg()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);

        // Active progress bar with a URL — WarningFg colours the URL slug
        var bar = StatusBarRenderer.FormatProgressBar(3, 10, p, isActive: true,
            currentUrl: "https://example.com/some-article-slug");

        bar.Should().Contain("\x1b[38;5;214m",
            "active progress bar should use WarningFg amber (214) for the URL slug");
        bar.Should().NotContain("\x1b[38;5;220m",
            "progress bar should not use hardcoded yellow (220)");
    }

    #endregion

    #region Test 5: Borders.DimmedRule uses DimFg (ANSI 22), not SecondaryText (ANSI 34)

    [Fact]
    public void DimmedRule_UsesDimFg_NotSecondaryText()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);

        var rule = Borders.DimmedRule(p, 40);

        rule.Should().Contain("\x1b[38;5;22m",
            "DimmedRule should use DimFg dim green (22)");
        rule.Should().NotContain("\x1b[38;5;34m",
            "DimmedRule should not use SecondaryText (34) — it should use the dedicated DimFg role");
    }

    #endregion

    #region Test 6: BuildHeadlineLines uses HeaderTitleFg (ANSI 212), not hardcoded white (ANSI 15)

    [Fact]
    public void BuildHeadlineLines_UsesHeaderTitleFg()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);
        var content = ReadableContent.Create(
            "Test Article Title",
            "Some article body text here.",
            new List<string> { "Some article body text here." });

        var headlineLines = LineCacheManager.BuildHeadlineLines(content, 80, p);

        var allText = string.Join("\n", headlineLines);
        allText.Should().Contain("\x1b[38;5;212m",
            "headline title should use HeaderTitleFg pink (212)");
        allText.Should().NotContain("\x1b[38;5;15m",
            "headline title should not use hardcoded white (15)");
    }

    #endregion

    #region Test 7: All 4 themes have non-null semantic colors

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void AllThemes_HaveNonNull_SemanticColors(ThemeName theme)
    {
        var p = BuiltInThemes.Get(theme);

        p.AccentFg.Should().NotBeNull($"{theme} should define AccentFg");
        p.DimFg.Should().NotBeNull($"{theme} should define DimFg");
        p.MutedFg.Should().NotBeNull($"{theme} should define MutedFg");
        p.SuccessFg.Should().NotBeNull($"{theme} should define SuccessFg");
        p.CelebrationFg.Should().NotBeNull($"{theme} should define CelebrationFg");
        p.WarningFg.Should().NotBeNull($"{theme} should define WarningFg");
        p.ReaderCursorFg.Should().NotBeNull($"{theme} should define ReaderCursorFg");
    }

    #endregion

    #region Test 9: Amber palette has intentional semantic ANSI codes

    [Fact]
    public void AmberPalette_HasIntentionalSemanticCodes()
    {
        var p = BuiltInThemes.Get(ThemeName.Amber);

        p.GetAccentFg().AnsiFg.Should().Be("\x1b[38;5;229m", "AccentFg should be bright warm yellow (229)");
        p.GetDimFg().AnsiFg.Should().Be("\x1b[38;5;58m", "DimFg should be very dark amber/brown (58)");
        p.GetMutedFg().AnsiFg.Should().Be("\x1b[38;5;130m", "MutedFg should be dim amber (130)");
        p.GetSuccessFg().AnsiFg.Should().Be("\x1b[38;5;148m", "SuccessFg should be bright yellow-green (148)");
        p.GetCelebrationFg().AnsiFg.Should().Be("\x1b[38;5;208m", "CelebrationFg should be warm orange (208)");
        p.GetWarningFg().AnsiFg.Should().Be("\x1b[38;5;196m", "WarningFg should be red (196)");
        p.GetReaderCursorFg().AnsiFg.Should().Be("\x1b[38;5;214m", "ReaderCursorFg should be warm amber (214)");
    }

    #endregion

    #region Test 10: Dracula palette has intentional semantic ANSI codes

    [Fact]
    public void DraculaPalette_HasIntentionalSemanticCodes()
    {
        var p = BuiltInThemes.Get(ThemeName.Dracula);

        p.GetAccentFg().AnsiFg.Should().Be("\x1b[38;5;117m", "AccentFg should be Dracula cyan (117)");
        p.GetDimFg().AnsiFg.Should().Be("\x1b[38;5;236m", "DimFg should be very dark gray (236)");
        p.GetMutedFg().AnsiFg.Should().Be("\x1b[38;5;61m", "MutedFg should be Dracula comment gray (61)");
        p.GetSuccessFg().AnsiFg.Should().Be("\x1b[38;5;84m", "SuccessFg should be Dracula green (84)");
        p.GetCelebrationFg().AnsiFg.Should().Be("\x1b[38;5;212m", "CelebrationFg should be Dracula pink (212)");
        p.GetWarningFg().AnsiFg.Should().Be("\x1b[38;5;220m", "WarningFg should be Dracula yellow (220)");
        p.GetReaderCursorFg().AnsiFg.Should().Be("\x1b[38;5;141m", "ReaderCursorFg should be Dracula purple (141)");
    }

    #endregion

    #region Test 11: Light palette has intentional semantic ANSI codes

    [Fact]
    public void LightPalette_HasIntentionalSemanticCodes()
    {
        var p = BuiltInThemes.Get(ThemeName.Light);

        p.GetAccentFg().AnsiFg.Should().Be("\x1b[38;5;30m", "AccentFg should be dark teal (30)");
        p.GetDimFg().AnsiFg.Should().Be("\x1b[38;5;250m", "DimFg should be light gray (250)");
        p.GetMutedFg().AnsiFg.Should().Be("\x1b[38;5;246m", "MutedFg should be medium gray (246)");
        p.GetSuccessFg().AnsiFg.Should().Be("\x1b[38;5;28m", "SuccessFg should be dark green (28)");
        p.GetCelebrationFg().AnsiFg.Should().Be("\x1b[38;5;162m", "CelebrationFg should be dark magenta (162)");
        p.GetWarningFg().AnsiFg.Should().Be("\x1b[38;5;172m", "WarningFg should be dark amber (172)");
        p.GetReaderCursorFg().AnsiFg.Should().Be("\x1b[38;5;24m", "ReaderCursorFg should be subtle dark blue (24)");
    }

    #endregion

    #region Test 8: SearchHighlight uses blue-teal bg (ANSI 24), not green bg (ANSI 46)

    [Fact]
    public void SearchHighlight_UsesBlueTeelBg_NotGreenBg()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);

        // Verify the palette value directly
        p.SearchHighlightBg.AnsiBg.Should().Be("\x1b[48;5;24m",
            "SearchHighlightBg should be blue-teal (24)");
        p.SearchHighlightBg.AnsiBg.Should().NotBe("\x1b[48;5;46m",
            "SearchHighlightBg should NOT be old neon green (46)");

        // Also verify via rendered output by calling WriteSearchHighlighted through WriteLineWithHighlight
        var output = CaptureConsoleOutput(() =>
            _helpers.WriteLineWithHighlight("Hello world search test", "search", p));

        output.Should().Contain("\x1b[48;5;24m",
            "search highlight rendered output should use blue-teal bg (24)");
        output.Should().NotContain("\x1b[48;5;46m",
            "search highlight rendered output should NOT use old green bg (46)");
    }

    #endregion
}
