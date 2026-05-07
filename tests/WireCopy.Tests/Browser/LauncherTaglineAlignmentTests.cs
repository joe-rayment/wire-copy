// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-usr3: the launcher tagline ("All copy, no clutter.") starts
/// at the same column as the leftmost glyph of the wordmark above it.
/// On the large wordmark the W is at column 6 of <c>Wordmark[0]</c>; on
/// the narrow fallback the title "Wire Copy" sits at column 1.
/// </summary>
[Trait("Category", "Unit")]
public class LauncherTaglineAlignmentTests
{
    private static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[0-9;]*m", string.Empty);

    [Fact]
    public void TaglineLeftEdge_LargeWordmark_AlignsWithWGlyph()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
        var lines = LauncherRenderer.BuildHeaderLines(width: 100, palette, showSetupHint: false);

        // Header rows: 0 top border · 1 blank · 2..7 wordmark (6 rows) · 8 tagline · 9 setup-hint/blank · 10 bottom border
        var wordmarkRow = StripAnsi(lines[2]);
        var taglineRow = StripAnsi(lines[8]);

        var wordmarkWPos = wordmarkRow.IndexOf("██╗", System.StringComparison.Ordinal);
        var taglineTextPos = taglineRow.IndexOf("All copy", System.StringComparison.Ordinal);

        wordmarkWPos.Should().BeGreaterThan(0, "the wordmark row should contain the W glyph");
        taglineTextPos.Should().Be(wordmarkWPos,
            "the tagline's left edge should sit directly under the W in the wordmark");
    }

    [Fact]
    public void TaglineLeftEdge_NarrowFallback_KeepsSingleSpacePad()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
        var lines = LauncherRenderer.BuildHeaderLines(width: 80, palette, showSetupHint: false);

        // Narrow header rows: 0 top border · 1 title · 2 tagline · 3 blank · 4 bottom border
        var titleRow = StripAnsi(lines[1]);
        var taglineRow = StripAnsi(lines[2]);

        var titleWPos = titleRow.IndexOf("Wire Copy", System.StringComparison.Ordinal);
        var taglineTextPos = taglineRow.IndexOf("All copy", System.StringComparison.Ordinal);

        titleWPos.Should().BeGreaterThan(0);
        taglineTextPos.Should().Be(titleWPos,
            "narrow fallback aligns tagline under the W in the single-line title");
    }
}
