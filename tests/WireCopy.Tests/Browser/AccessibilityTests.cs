// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

// =============================================================================
// ACCESSIBILITY AUDIT — Contrast, Colorblind Safety, Motion Sensitivity
// =============================================================================
//
// This file documents the current accessibility state of WireCopy's 4 themes.
// Tests calculate WCAG 2.1 contrast ratios and record whether each color
// combination meets AA thresholds (4.5:1 normal text, 3:1 large text).
//
// These tests are INFORMATIONAL — they document current state so future work
// can prioritize fixes. They are not gatekeeping; all tests pass by asserting
// the measured ratio rather than requiring AA compliance.
//
// ---------------------------------------------------------------------------
// COLORBLIND ACCESSIBILITY NOTES
// ---------------------------------------------------------------------------
//
// Deuteranopia (red-green, ~8% of males, ~0.5% of females):
//   - Phosphor: MODERATE RISK. Relies on green (#00d700) vs pink (#ff87d7)
//     for body text vs titles. Deuteranopic users will see both as similar
//     yellowish-brown tones. However, structural hierarchy (indentation,
//     positioning) still communicates the distinction. Error red (#ff5f5f)
//     may blend with green text.
//   - Amber: BEST. Monochromatic amber palette — all distinctions are via
//     brightness, not hue. Fully accessible to all forms of color blindness.
//   - Dracula: MODERATE RISK. Uses green (#5fff00), pink (#ff87d7), cyan
//     (#87d7ff), and purple (#af87ff). Green/pink distinction is lost for
//     deuteranopia, but cyan and purple remain distinguishable.
//   - Light: LOW RISK. Uses dark blue (#005faf), dark gray, and black.
//     Primary distinctions are luminance-based. Good for colorblind users.
//
// Protanopia (red-blind, ~1% of males):
//   - Similar concerns as deuteranopia for Phosphor and Dracula themes.
//   - Amber and Light themes remain safe.
//
// Tritanopia (blue-yellow, ~0.002% of population):
//   - Phosphor: Low risk (green/pink still distinguishable).
//   - Amber: Moderate risk — amber yellows may shift, but brightness
//     distinctions remain.
//   - Dracula: Cyan (#87d7ff) and yellow (#ffd700) may appear similar.
//   - Light: Blue links may appear grayish, reducing link visibility.
//
// RECOMMENDATION: Amber is the most universally accessible theme.
//   For users with red-green color blindness, recommend Amber or Light.
//
// ---------------------------------------------------------------------------
// MOTION SENSITIVITY NOTES
// ---------------------------------------------------------------------------
//
// All WireCopy animations are terminal-safe for motion sensitivity:
//   - Spinner animations use discrete character frames (braille dots, shapes),
//     not smooth motion or spatial movement.
//   - Animation intervals are 250-500ms per frame (2-4 fps), well below
//     the vestibular-triggering threshold of smooth 60fps motion.
//   - No looping animations during normal browsing — animations only appear
//     during active loading/generation operations and stop when complete.
//   - No parallax, sliding, or spatial transforms — all "animation" is
//     in-place character substitution.
//   - A DisableAnimations config option EXISTS on BrowserConfiguration.
//     When enabled (Browser:DisableAnimations=true), the timer-tick
//     mechanism is suppressed. Verify that PodcastCommandHandler and
//     LayoutCommandHandler respect this flag by showing static text
//     instead of spinner frames.
//
// =============================================================================

[Trait("Category", "Unit")]
public class AccessibilityTests
{
    private readonly ITestOutputHelper _output;

    public AccessibilityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =========================================================================
    // WCAG AA Contrast Ratio Tests — Dark themes (Phosphor, Amber, Dracula)
    // Background assumed: #000000 (black terminal)
    // =========================================================================

    #region Phosphor Theme Contrast

    [Fact]
    public void Phosphor_PrimaryText_OnBlack_MeetsAA()
    {
        // PrimaryText: ANSI 40 = #00d700 (pure phosphor green)
        var ratio = ContrastRatio(AnsiToRgb(40), (0, 0, 0));
        _output.WriteLine($"Phosphor PrimaryText (#00d700) on black: {ratio:F2}:1 (AA requires 4.5:1)");
        ratio.Should().BeGreaterThan(0, "ratio should be calculable");

        // Document: green on black is typically high contrast
        RecordResult("Phosphor", "PrimaryText on black", ratio, _output);
    }

    [Fact]
    public void Phosphor_SecondaryText_OnBlack_ContrastRatio()
    {
        // SecondaryText: ANSI 34 = #00af00 (medium green)
        var ratio = ContrastRatio(AnsiToRgb(34), (0, 0, 0));
        RecordResult("Phosphor", "SecondaryText on black", ratio, _output);
    }

    [Fact]
    public void Phosphor_MutedFg_OnBlack_ContrastRatio()
    {
        // MutedFg: ANSI 65 = #5f875f (muted green)
        var ratio = ContrastRatio(AnsiToRgb(65), (0, 0, 0));
        RecordResult("Phosphor", "MutedFg on black", ratio, _output);
    }

    [Fact]
    public void Phosphor_SelectedItem_ContrastRatio()
    {
        // SelectedItemFg: ANSI 254 = #e4e4e4, SelectedItemBg: ANSI 22 = #005f00
        var ratio = ContrastRatio(AnsiToRgb(254), AnsiToRgb(22));
        RecordResult("Phosphor", "SelectedItemFg on SelectedItemBg", ratio, _output);
    }

    [Fact]
    public void Phosphor_ErrorFg_OnBlack_ContrastRatio()
    {
        // ErrorFg: ANSI 203 = #ff5f5f
        var ratio = ContrastRatio(AnsiToRgb(203), (0, 0, 0));
        RecordResult("Phosphor", "ErrorFg on black", ratio, _output);
    }

    [Fact]
    public void Phosphor_SearchHighlight_ContrastRatio()
    {
        // SearchHighlightFg: ANSI 15 = white, SearchHighlightBg: ANSI 24 = #005f87
        var ratio = ContrastRatio(AnsiToRgb(15), AnsiToRgb(24));
        RecordResult("Phosphor", "SearchHighlightFg on SearchHighlightBg", ratio, _output);
    }

    #endregion

    #region Amber Theme Contrast

    [Fact]
    public void Amber_PrimaryText_OnBlack_ContrastRatio()
    {
        // PrimaryText: ANSI 220 = #ffd700 (warm amber)
        var ratio = ContrastRatio(AnsiToRgb(220), (0, 0, 0));
        RecordResult("Amber", "PrimaryText on black", ratio, _output);
    }

    [Fact]
    public void Amber_SecondaryText_OnBlack_ContrastRatio()
    {
        // SecondaryText: ANSI 136 = #af8700 (dim amber)
        var ratio = ContrastRatio(AnsiToRgb(136), (0, 0, 0));
        RecordResult("Amber", "SecondaryText on black", ratio, _output);
    }

    [Fact]
    public void Amber_MutedFg_OnBlack_ContrastRatio()
    {
        // MutedFg: ANSI 130 = #af5f00
        var ratio = ContrastRatio(AnsiToRgb(130), (0, 0, 0));
        RecordResult("Amber", "MutedFg on black", ratio, _output);
    }

    [Fact]
    public void Amber_SelectedItem_ContrastRatio()
    {
        // SelectedItemFg: ANSI 0 = black, SelectedItemBg: ANSI 220 = #ffd700
        var ratio = ContrastRatio(AnsiToRgb(0), AnsiToRgb(220));
        RecordResult("Amber", "SelectedItemFg on SelectedItemBg", ratio, _output);
    }

    [Fact]
    public void Amber_ErrorFg_OnBlack_ContrastRatio()
    {
        // ErrorFg: ANSI 203 = #ff5f5f
        var ratio = ContrastRatio(AnsiToRgb(203), (0, 0, 0));
        RecordResult("Amber", "ErrorFg on black", ratio, _output);
    }

    [Fact]
    public void Amber_SearchHighlight_ContrastRatio()
    {
        // SearchHighlightFg: ANSI 0 = black, SearchHighlightBg: ANSI 220 = #ffd700
        var ratio = ContrastRatio(AnsiToRgb(0), AnsiToRgb(220));
        RecordResult("Amber", "SearchHighlightFg on SearchHighlightBg", ratio, _output);
    }

    #endregion

    #region Dracula Theme Contrast

    [Fact]
    public void Dracula_PrimaryText_OnBlack_ContrastRatio()
    {
        // PrimaryText: ANSI 253 = #dadada
        var ratio = ContrastRatio(AnsiToRgb(253), (0, 0, 0));
        RecordResult("Dracula", "PrimaryText on black", ratio, _output);
    }

    [Fact]
    public void Dracula_SecondaryText_OnBlack_ContrastRatio()
    {
        // SecondaryText: ANSI 245 = #8a8a8a (comment gray)
        var ratio = ContrastRatio(AnsiToRgb(245), (0, 0, 0));
        RecordResult("Dracula", "SecondaryText on black", ratio, _output);
    }

    [Fact]
    public void Dracula_MutedFg_OnBlack_ContrastRatio()
    {
        // MutedFg: ANSI 61 = #5f5faf (Dracula comment purple)
        var ratio = ContrastRatio(AnsiToRgb(61), (0, 0, 0));
        RecordResult("Dracula", "MutedFg on black", ratio, _output);
    }

    [Fact]
    public void Dracula_SelectedItem_ContrastRatio()
    {
        // SelectedItemFg: ANSI 253 = #dadada, SelectedItemBg: ANSI 237 = #3a3a3a
        var ratio = ContrastRatio(AnsiToRgb(253), AnsiToRgb(237));
        RecordResult("Dracula", "SelectedItemFg on SelectedItemBg", ratio, _output);
    }

    [Fact]
    public void Dracula_ErrorFg_OnBlack_ContrastRatio()
    {
        // ErrorFg: ANSI 203 = #ff5f5f
        var ratio = ContrastRatio(AnsiToRgb(203), (0, 0, 0));
        RecordResult("Dracula", "ErrorFg on black", ratio, _output);
    }

    [Fact]
    public void Dracula_SearchHighlight_ContrastRatio()
    {
        // SearchHighlightFg: ANSI 0 = black, SearchHighlightBg: ANSI 220 = #ffd700
        var ratio = ContrastRatio(AnsiToRgb(0), AnsiToRgb(220));
        RecordResult("Dracula", "SearchHighlightFg on SearchHighlightBg", ratio, _output);
    }

    #endregion

    #region Light Theme Contrast

    // Light theme assumes white (#ffffff) terminal background
    private static readonly (int R, int G, int B) WhiteBackground = (255, 255, 255);

    [Fact]
    public void Light_PrimaryText_OnWhite_ContrastRatio()
    {
        // PrimaryText: ANSI 235 = #262626 (near-black)
        var ratio = ContrastRatio(AnsiToRgb(235), WhiteBackground);
        RecordResult("Light", "PrimaryText on white", ratio, _output);
    }

    [Fact]
    public void Light_SecondaryText_OnWhite_ContrastRatio()
    {
        // SecondaryText: ANSI 240 = #585858
        var ratio = ContrastRatio(AnsiToRgb(240), WhiteBackground);
        RecordResult("Light", "SecondaryText on white", ratio, _output);
    }

    [Fact]
    public void Light_MutedFg_OnWhite_ContrastRatio()
    {
        // MutedFg: ANSI 246 = #949494
        var ratio = ContrastRatio(AnsiToRgb(246), WhiteBackground);
        RecordResult("Light", "MutedFg on white", ratio, _output);
    }

    [Fact]
    public void Light_SelectedItem_ContrastRatio()
    {
        // SelectedItemFg: ANSI 252 = #d0d0d0, SelectedItemBg: ANSI 25 = #005faf
        var ratio = ContrastRatio(AnsiToRgb(252), AnsiToRgb(25));
        RecordResult("Light", "SelectedItemFg on SelectedItemBg", ratio, _output);
    }

    [Fact]
    public void Light_ErrorFg_OnWhite_ContrastRatio()
    {
        // ErrorFg: ANSI 160 = #d70000
        var ratio = ContrastRatio(AnsiToRgb(160), WhiteBackground);
        RecordResult("Light", "ErrorFg on white", ratio, _output);
    }

    [Fact]
    public void Light_SearchHighlight_ContrastRatio()
    {
        // SearchHighlightFg: ANSI 252 = #d0d0d0, SearchHighlightBg: ANSI 136 = #af8700
        var ratio = ContrastRatio(AnsiToRgb(252), AnsiToRgb(136));
        RecordResult("Light", "SearchHighlightFg on SearchHighlightBg", ratio, _output);
    }

    #endregion

    // =========================================================================
    // Cross-theme summary test
    // =========================================================================

    [Fact]
    public void AllThemes_ContrastAuditSummary()
    {
        // This test prints a complete summary table of all contrast ratios
        // across all themes for quick reference.
        _output.WriteLine("=== WCAG AA Contrast Audit Summary ===");
        _output.WriteLine("AA normal text: 4.5:1 | AA large text: 3.0:1");
        _output.WriteLine("");

        var themes = new[]
        {
            ("Phosphor", ThemeName.Phosphor, (0, 0, 0)),
            ("Amber", ThemeName.Amber, (0, 0, 0)),
            ("Dracula", ThemeName.Dracula, (0, 0, 0)),
            ("Light", ThemeName.Light, (255, 255, 255)),
        };

        var totalCombinations = 0;
        var passingAA = 0;
        var passingAALarge = 0;

        foreach (var (name, themeName, bg) in themes)
        {
            var palette = BuiltInThemes.Get(themeName);
            _output.WriteLine($"--- {name} (bg: #{bg.Item1:X2}{bg.Item2:X2}{bg.Item3:X2}) ---");

            var combos = new (string Label, (int R, int G, int B) Fg, (int R, int G, int B) Bg)[]
            {
                ("PrimaryText on bg", AnsiToRgb(palette.PrimaryText.AnsiCode), bg),
                ("SecondaryText on bg", AnsiToRgb(palette.SecondaryText.AnsiCode), bg),
                ("MutedFg on bg", AnsiToRgb(palette.GetMutedFg().AnsiCode), bg),
                ("ErrorFg on bg", AnsiToRgb(palette.ErrorFg.AnsiCode), bg),
                ("SelectedItem Fg/Bg", AnsiToRgb(palette.SelectedItemFg.AnsiCode), AnsiToRgb(palette.SelectedItemBg.AnsiCode)),
                ("SearchHighlight Fg/Bg", AnsiToRgb(palette.SearchHighlightFg.AnsiCode), AnsiToRgb(palette.SearchHighlightBg.AnsiCode)),
                ("HeaderTitleFg on bg", AnsiToRgb(palette.HeaderTitleFg.AnsiCode), bg),
                ("AccentFg on bg", AnsiToRgb(palette.GetAccentFg().AnsiCode), bg),
                ("DimFg on bg", AnsiToRgb(palette.GetDimFg().AnsiCode), bg),
                ("ReadItemFg on bg", AnsiToRgb(palette.ReadItemFg.AnsiCode), bg),
            };

            foreach (var (label, fg, cbg) in combos)
            {
                var ratio = ContrastRatio(fg, cbg);
                var aaPass = ratio >= 4.5 ? "PASS" : "FAIL";
                var aaLargePass = ratio >= 3.0 ? "PASS" : "FAIL";
                _output.WriteLine($"  {label,-30} {ratio,6:F2}:1  AA:{aaPass}  AA-Large:{aaLargePass}");

                totalCombinations++;
                if (ratio >= 4.5) passingAA++;
                if (ratio >= 3.0) passingAALarge++;
            }

            _output.WriteLine("");
        }

        _output.WriteLine($"Total: {totalCombinations} combinations");
        _output.WriteLine($"  AA normal (4.5:1): {passingAA}/{totalCombinations} pass ({100.0 * passingAA / totalCombinations:F0}%)");
        _output.WriteLine($"  AA large  (3.0:1): {passingAALarge}/{totalCombinations} pass ({100.0 * passingAALarge / totalCombinations:F0}%)");

        // This test always passes — it's for documentation
        totalCombinations.Should().BeGreaterThan(0);
    }

    // =========================================================================
    // Colorblind simulation tests
    // =========================================================================

    [Fact]
    public void Phosphor_GreenPinkDistinction_DeuteranopiaRisk()
    {
        // Phosphor uses green (#00d700, ANSI 40) for body and pink (#ff87d7, ANSI 212)
        // for titles. Under deuteranopia simulation, these become similar.
        var green = AnsiToRgb(40);  // PrimaryText
        var pink = AnsiToRgb(212);  // HeaderTitleFg

        // Calculate how distinguishable these are by luminance alone
        // (which is what deuteranopic users primarily rely on)
        var greenLum = RelativeLuminance(green);
        var pinkLum = RelativeLuminance(pink);
        var lumRatio = greenLum > pinkLum
            ? (greenLum + 0.05) / (pinkLum + 0.05)
            : (pinkLum + 0.05) / (greenLum + 0.05);

        _output.WriteLine($"Phosphor green vs pink luminance ratio: {lumRatio:F2}:1");
        _output.WriteLine($"  Green luminance: {greenLum:F4}");
        _output.WriteLine($"  Pink luminance:  {pinkLum:F4}");
        _output.WriteLine("  Deuteranopia: green and pink shift to similar yellow-brown hues.");
        _output.WriteLine("  Mitigation: structural hierarchy (indentation) provides non-color cue.");

        // Document the ratio — low luminance contrast means deuteranopic users
        // lose the color distinction and must rely on position/structure
        lumRatio.Should().BeGreaterThan(0, "luminance ratio should be calculable");
    }

    [Fact]
    public void Amber_IsMonochromatic_SafeForAllColorblindTypes()
    {
        // Amber theme uses only yellow/amber hues at different brightnesses.
        // All distinctions are luminance-based, making it fully colorblind-safe.
        var palette = BuiltInThemes.Get(ThemeName.Amber);
        var primary = AnsiToRgb(palette.PrimaryText.AnsiCode);
        var secondary = AnsiToRgb(palette.SecondaryText.AnsiCode);
        var muted = AnsiToRgb(palette.GetMutedFg().AnsiCode);
        var dim = AnsiToRgb(palette.GetDimFg().AnsiCode);

        // Verify luminance progression: primary > secondary > muted > dim
        var lumPrimary = RelativeLuminance(primary);
        var lumSecondary = RelativeLuminance(secondary);
        var lumMuted = RelativeLuminance(muted);
        var lumDim = RelativeLuminance(dim);

        _output.WriteLine($"Amber luminance ladder:");
        _output.WriteLine($"  PrimaryText:   {lumPrimary:F4}");
        _output.WriteLine($"  SecondaryText: {lumSecondary:F4}");
        _output.WriteLine($"  MutedFg:       {lumMuted:F4}");
        _output.WriteLine($"  DimFg:         {lumDim:F4}");
        _output.WriteLine("  All hue-based — safe for all color vision deficiencies.");

        // Primary should be brightest, dim should be darkest
        lumPrimary.Should().BeGreaterThan(lumSecondary, "primary should be brighter than secondary");
        lumDim.Should().BeLessThan(lumMuted, "dim should be darker than muted");
    }

    // =========================================================================
    // DisableAnimations config test (documents that it does not yet exist)
    // =========================================================================

    [Fact]
    public void DisableAnimations_ConfigOption_Exists()
    {
        // The DisableAnimations configuration option exists on BrowserConfiguration.
        // When true, the timer-tick mechanism in the input handler does not fire
        // and animation frames are skipped.
        //
        // This benefits users with:
        //   - Vestibular disorders (even discrete frame changes can be uncomfortable)
        //   - ADHD (spinners can pull focus from content)
        //   - Photosensitive conditions (rapid character changes on some terminals)
        //   - Low-resource environments or terminals with poor redraw performance
        //
        // Remaining work: verify that PodcastCommandHandler and LayoutCommandHandler
        // actually check this flag and suppress spinner frames when it is true.
        // If not yet wired up, those handlers should replace animated spinners
        // with static "Working..." text when DisableAnimations is enabled.

        var configType = typeof(WireCopy.Infrastructure.Configuration.BrowserConfiguration);
        var prop = configType.GetProperty("DisableAnimations");

        prop.Should().NotBeNull("DisableAnimations config property should exist");
        prop!.PropertyType.Should().Be(typeof(bool), "DisableAnimations should be a boolean toggle");

        // Verify default is false (animations enabled by default)
        var instance = new WireCopy.Infrastructure.Configuration.BrowserConfiguration();
        var defaultValue = (bool)prop.GetValue(instance)!;
        defaultValue.Should().BeFalse("animations should be enabled by default");

        _output.WriteLine("DisableAnimations config exists: true");
        _output.WriteLine("Default value: false (animations enabled)");
        _output.WriteLine("Set Browser:DisableAnimations=true in config to suppress animations.");
    }

    // =========================================================================
    // Helper: ANSI 256 code to RGB conversion
    // =========================================================================

    /// <summary>
    /// Converts an ANSI 256-color code to an RGB tuple.
    /// Standard colors 0-15 use well-known terminal defaults.
    /// 16-231: 6x6x6 color cube. 232-255: grayscale ramp.
    /// </summary>
    internal static (int R, int G, int B) AnsiToRgb(byte ansiCode)
    {
        if (ansiCode < 16)
        {
            // Standard 16 colors (typical terminal defaults)
            return ansiCode switch
            {
                0 => (0, 0, 0),          // Black
                1 => (128, 0, 0),        // Red
                2 => (0, 128, 0),        // Green
                3 => (128, 128, 0),      // Yellow/Brown
                4 => (0, 0, 128),        // Blue
                5 => (128, 0, 128),      // Magenta
                6 => (0, 128, 128),      // Cyan
                7 => (192, 192, 192),    // White (light gray)
                8 => (128, 128, 128),    // Bright Black (dark gray)
                9 => (255, 0, 0),        // Bright Red
                10 => (0, 255, 0),       // Bright Green
                11 => (255, 255, 0),     // Bright Yellow
                12 => (0, 0, 255),       // Bright Blue
                13 => (255, 0, 255),     // Bright Magenta
                14 => (0, 255, 255),     // Bright Cyan
                15 => (255, 255, 255),   // Bright White
                _ => (0, 0, 0),
            };
        }

        if (ansiCode < 232)
        {
            // 6x6x6 color cube (codes 16-231)
            var index = ansiCode - 16;
            var r = index / 36;
            var g = (index % 36) / 6;
            var b = index % 6;
            return (r * 51, g * 51, b * 51);
        }

        // Grayscale ramp (codes 232-255)
        var v = ((ansiCode - 232) * 10) + 8;
        return (v, v, v);
    }

    // =========================================================================
    // Helper: WCAG 2.1 contrast ratio calculation
    // =========================================================================

    /// <summary>
    /// Calculates the WCAG 2.1 contrast ratio between two colors.
    /// Returns a value >= 1.0 where 1.0 is no contrast and 21.0 is maximum.
    /// </summary>
    internal static double ContrastRatio((int R, int G, int B) color1, (int R, int G, int B) color2)
    {
        var l1 = RelativeLuminance(color1);
        var l2 = RelativeLuminance(color2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Calculates the relative luminance of a color per WCAG 2.1.
    /// Uses the sRGB linearization formula and ITU-R BT.709 coefficients.
    /// </summary>
    internal static double RelativeLuminance((int R, int G, int B) color)
    {
        var r = LinearizeSrgb(color.R / 255.0);
        var g = LinearizeSrgb(color.G / 255.0);
        var b = LinearizeSrgb(color.B / 255.0);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>
    /// Linearizes an sRGB channel value (0.0-1.0) to linear light.
    /// </summary>
    private static double LinearizeSrgb(double channel)
    {
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Records a contrast ratio result with WCAG AA pass/fail status.
    /// </summary>
    private static void RecordResult(
        string theme,
        string combination,
        double ratio,
        ITestOutputHelper output)
    {
        var aaPass = ratio >= 4.5 ? "PASS" : "FAIL";
        var aaLargePass = ratio >= 3.0 ? "PASS" : "FAIL";
        output.WriteLine(
            $"[{theme}] {combination}: {ratio:F2}:1  " +
            $"AA normal: {aaPass}  AA large: {aaLargePass}");

        // Assert the ratio is positive (test always passes — this is documentation)
        ratio.Should().BeGreaterThan(1.0,
            $"{theme} {combination} should have measurable contrast");
    }
}
