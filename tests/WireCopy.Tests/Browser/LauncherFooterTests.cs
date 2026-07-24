// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-ej1i + workspace-e86u (Launcher.dc.html): launcher footer
/// behaviour. The footer is two lines — a dim top rule that caps the grid
/// (replaced by the transient status message / scheduled-run badge when one is
/// active) and the `[key]:action` hint line with a right-aligned bookmark
/// count. `?` is labelled "all keys" (the footer only shows five of the
/// launcher's bindings).
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class LauncherFooterTests
{
    [Fact]
    public void RenderFooter_LabelsHelpKeyAsAllKeys()
    {
        var (output, _) = CaptureFooter();
        var stripped = StripAnsi(output);

        stripped.Should().Contain("[?]:all keys",
            "workspace-ej1i.3: the footer must advertise that ? reveals the full key list");
        stripped.Should().NotContain("[?]:help",
            "the vague 'help' label hid that more keys exist beyond the footer");
    }

    [Fact]
    public void RenderFooter_UsesKeyColonActionHints()
    {
        // Launcher.dc.html (workspace-e86u): hints read `[Enter]:open` — the
        // key wears the interactive accent, the chrome is muted.
        var (output, _) = CaptureFooter();
        var stripped = StripAnsi(output);

        stripped.Should().Contain("[Enter]:open");
        stripped.Should().Contain("[1-9]:jump");
        stripped.Should().Contain("[o]:go to url");
        stripped.Should().Contain("[a]:add");
    }

    [Fact]
    public void RenderFooter_ShowsRightAlignedBookmarkCount()
    {
        var (output, _) = CaptureFooter(bookmarkCount: 6);
        var hintLine = SplitScreenLines(output).First(l => l.Contains("[Enter]:open"));

        hintLine.TrimEnd().Should().EndWith("6 bookmarks",
            "Launcher.dc.html puts the dim bookmark count at the footer's right edge");
    }

    [Fact]
    public void RenderFooter_SingularBookmarkCount()
    {
        var (output, _) = CaptureFooter(bookmarkCount: 1);

        StripAnsi(output).Should().Contain("1 bookmark")
            .And.NotContain("1 bookmarks");
    }

    [Fact]
    public void RenderFooter_PaintsStatusMessage()
    {
        var (output, linesWritten) = CaptureFooter(statusMessage: "Already at top");

        StripAnsi(output).Should().Contain("Already at top",
            "launcher status feedback must be visible — SetStatusMessage was previously swallowed on this screen");
        linesWritten.Should().Be(2, "status message occupies the rule's line above the hints");
    }

    [Fact]
    public void RenderFooter_NoStatusMessage_DrawsTopRule()
    {
        // Launcher.dc.html (workspace-e86u): the grid's bottom row draws no
        // per-cell rule; the footer's top rule is what caps the grid, so it
        // must span the content width whenever no status/badge claims the line.
        var (output, linesWritten) = CaptureFooter();
        var lines = SplitScreenLines(output);

        linesWritten.Should().Be(2, "footer is rule line + hint line");
        lines[0].Should().Be(new string('─', 78), "the rule spans the content width (80 - 2)");
    }

    [Fact]
    public void RenderFooter_TransientStatus_TakesPriorityOverScheduledRunBadge()
    {
        // workspace-xx61 (511u): the status message is transient (a keypress just
        // happened); the badge is persistent and returns on the next render, so
        // the status wins the rule's line or feedback is lost.
        var (output, linesWritten) = CaptureFooter(
            scheduledRunBadge: "1 scheduled run failed",
            statusMessage: "Already at top");
        var stripped = StripAnsi(output);

        stripped.Should().Contain("Already at top",
            "the transient status would be lost forever if the persistent badge won the slot");
        stripped.Should().NotContain("1 scheduled run failed",
            "only one auxiliary line exists; the badge returns next render");
        linesWritten.Should().Be(2);
    }

    private static (string Output, int LinesWritten) CaptureFooter(
        string? scheduledRunBadge = null, string? statusMessage = null, int bookmarkCount = 6)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = 24 };
        var renderer = new LauncherRenderer(helpers, themeProvider);

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderFooter(80, bookmarkCount, scheduledRunBadge, statusMessage);
            return (sw.ToString(), helpers.LinesWritten);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string StripAnsi(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"\x1b\[[0-9;]*[A-Za-z]", string.Empty);
    }

    /// <summary>
    /// RenderHelpers positions each line with a cursor move + a leading ESC[K
    /// erase rather than a newline (cursor moves go through
    /// Console.SetCursorPosition and never reach the captured writer), so
    /// screen lines are recovered by splitting the raw output on the ESC[K
    /// that opens every line, then stripping the remaining SGR codes.
    /// </summary>
    private static string[] SplitScreenLines(string raw)
    {
        return raw
            .Split("\x1b[K")
            .Select(StripAnsi)
            .Where(l => l.Length > 0)
            .ToArray();
    }
}
