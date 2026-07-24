// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Launcher.dc.html masthead contract (workspace-pn5f, superseding the
/// workspace-usr3 under-the-W alignment): the wordmark AND the tagline are
/// both centred inside the masthead card, and the tagline reads exactly
/// "All copy, no nonsense · v{version}" — no trailing period, with the
/// version visible next to the dot rather than styled dim-on-dim.
/// </summary>
[Trait("Category", "Unit")]
public class LauncherTaglineAlignmentTests
{
    private static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[0-9;]*m", string.Empty);

    private static int CenterOf(string row, string content) =>
        row.IndexOf(content, System.StringComparison.Ordinal) + (content.Length / 2);

    [Fact]
    public void LargeWordmark_WordmarkAndTagline_AreCentredOnTheBoxAxis()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
        var lines = LauncherRenderer.BuildHeaderLines(width: 84, palette, showSetupHint: false);

        // Header rows (workspace-pn5f): 0 top border · 1 blank · 2..7 wordmark
        // · 8 blank · 9 tagline · 10 padding · 11 bottom border.
        lines.Should().HaveCount(12);

        var borderRow = StripAnsi(lines[0]);
        var wordmarkRow = StripAnsi(lines[2]);
        var taglineRow = StripAnsi(lines[9]);

        var boxLeft = borderRow.IndexOf('╭');
        var boxRight = borderRow.IndexOf('╮');
        var boxCenter = (boxLeft + boxRight) / 2;

        var art = wordmarkRow.Trim('│', ' ');
        var artCenter = CenterOf(wordmarkRow, art);
        artCenter.Should().BeCloseTo(boxCenter, 1,
            "the wordmark art must be centred inside the masthead card");

        var tagline = "All copy, no nonsense";
        taglineRow.Should().Contain(tagline);
        taglineRow.Should().NotContain(tagline + ".",
            "the design tagline carries no trailing period");
        var taglineText = taglineRow[taglineRow.IndexOf(tagline, System.StringComparison.Ordinal)..]
            .TrimEnd('│', ' ');
        var taglineCenter = CenterOf(taglineRow, taglineText);
        taglineCenter.Should().BeCloseTo(boxCenter, 1,
            "the tagline must be centred inside the masthead card");
    }

    [Fact]
    public void Tagline_CarriesVisibleVersionAfterCentredDot()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
        var lines = LauncherRenderer.BuildHeaderLines(width: 84, palette, showSetupHint: false);

        var taglineRaw = lines[9];
        var stripped = StripAnsi(taglineRaw);
        stripped.Should().MatchRegex(@"All copy, no nonsense · v\d+\.\d+",
            "tagline reads 'All copy, no nonsense · v{version}' per Launcher.dc.html");

        // The version must NOT wear the SGR dim attribute — dim + the dark
        // structural green rendered it invisible (the workspace-pn5f finding).
        taglineRaw.Should().NotContain("\x1b[2m",
            "no dim attribute anywhere in the tagline; the dot is dim GREEN (256-color), not faded");
    }

    [Fact]
    public void NarrowFallback_TitleAndTagline_AreCentred()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
        var lines = LauncherRenderer.BuildHeaderLines(width: 60, palette, showSetupHint: false);

        // Narrow header rows: 0 top border · 1 title · 2 tagline · 3 bottom border.
        lines.Should().HaveCount(4);

        var borderRow = StripAnsi(lines[0]);
        var titleRow = StripAnsi(lines[1]);
        var taglineRow = StripAnsi(lines[2]);

        var boxLeft = borderRow.IndexOf('╭');
        var boxRight = borderRow.IndexOf('╮');
        var boxCenter = (boxLeft + boxRight) / 2;

        CenterOf(titleRow, "Wire Copy").Should().BeCloseTo(boxCenter, 1,
            "narrow fallback centres the single-line title");
        var tagline = taglineRow[taglineRow.IndexOf("All copy", System.StringComparison.Ordinal)..]
            .TrimEnd('│', ' ');
        CenterOf(taglineRow, tagline).Should().BeCloseTo(boxCenter, 1,
            "narrow fallback centres the tagline");
    }
}
