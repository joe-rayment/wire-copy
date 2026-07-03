// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Cross-platform unit tests for the side-by-side sidecar tiling helpers (workspace-75ng.4).
/// The osascript runs only on a Mac (with Accessibility permission, confirmed there by the
/// user), but the command construction, the bounds parsing, and the complement geometry are
/// pure and fully testable here — same philosophy as <see cref="DockGeometry"/>.
/// </summary>
public class TerminalTilingTests
{
    [Fact]
    public void BuildGetBoundsScript_TargetsBundleId_AndAsksPositionAndSize()
    {
        var script = TerminalTiling.BuildGetBoundsScript("com.mitchellh.ghostty");

        script.Should().Contain("System Events");
        script.Should().Contain("bundle identifier is \"com.mitchellh.ghostty\"");
        script.Should().Contain("position of window 1");
        script.Should().Contain("size of window 1");
    }

    [Fact]
    public void BuildSetBoundsScript_SetsPositionThenSize_WithCoords()
    {
        var script = TerminalTiling.BuildSetBoundsScript(
            "com.apple.Terminal", new TerminalTiling.WindowRect(10, 20, 800, 1000));

        script.Should().Contain("bundle identifier is \"com.apple.Terminal\"");
        script.Should().Contain("set position of window 1 to {10, 20}");
        script.Should().Contain("set size of window 1 to {800, 1000}");

        // Position must precede size so the window lands on the right display before growing.
        script.IndexOf("set position", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("set size", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildScripts_EscapeQuotesAndBackslashes()
    {
        TerminalTiling.BuildGetBoundsScript("weird\"id\\x")
            .Should().Contain("bundle identifier is \"weird\\\"id\\\\x\"");
        TerminalTiling.BuildSetBoundsScript("weird\"id\\x", default)
            .Should().Contain("bundle identifier is \"weird\\\"id\\\\x\"");
        TerminalTiling.BuildRestoreMatchedWindowScript(
                "weird\"id\\x", default, default, 8)
            .Should().Contain("bundle identifier is \"weird\\\"id\\\\x\"");
    }

    // ---- workspace-9k27.6 (minor): the restore must target the window WE tiled, ----
    // ---- never `window 1` (a multi-window terminal's frontmost may differ).     ----

    [Fact]
    public void BuildRestoreMatchedWindowScript_MatchesTheTiledWindow_NotWindow1()
    {
        var script = TerminalTiling.BuildRestoreMatchedWindowScript(
            "com.mitchellh.ghostty",
            expectedTile: new TerminalTiling.WindowRect(0, 25, 1000, 875),
            restoreTo: new TerminalTiling.WindowRect(10, 20, 1600, 900),
            tolerancePx: 8);

        script.Should().NotContain("window 1", "the restore must never act on the frontmost window blindly");
        script.Should().Contain("repeat with w in windows", "every window is checked against the tile");
        script.Should().Contain("bundle identifier is \"com.mitchellh.ghostty\"");

        // The match compares against the TILE we set…
        script.Should().Contain("(0)").And.Contain("(25)").And.Contain("(1000)").And.Contain("(875)");
        script.Should().Contain("<= 8", "the tolerance guards against WM nudges of a few px");

        // …and the matched window is put back to the PRE-DOCK bounds, position before size.
        script.Should().Contain("set position of w to {10, 20}");
        script.Should().Contain("set size of w to {1600, 900}");
        script.IndexOf("set position", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("set size", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRestoreMatchedWindowScript_ReportsRestoredOrNoMatch()
    {
        var script = TerminalTiling.BuildRestoreMatchedWindowScript(
            "com.apple.Terminal", default, default, 8);

        // The caller decides logging (and whether the user's layout won) off these
        // two verdicts, so the script must print exactly one of them.
        script.Should().Contain("return \"" + TerminalTiling.RestoreResultRestored + "\"");
        script.Should().Contain("return \"" + TerminalTiling.RestoreResultNoMatch + "\"");
    }

    [Fact]
    public void BuildRestoreMatchedWindowScript_NegativeToleranceClampsToZero()
    {
        TerminalTiling.BuildRestoreMatchedWindowScript("com.apple.Terminal", default, default, -5)
            .Should().Contain("<= 0", "a negative tolerance must not produce an always-false match");
    }

    [Theory]
    [InlineData("0, 0, 1200, 800", 0, 0, 1200, 800)]
    [InlineData("-1920, 25, 800, 1000", -1920, 25, 800, 1000)] // a display left of primary
    [InlineData("  100 ,  50 , 640 , 480 ", 100, 50, 640, 480)] // tolerant of whitespace
    public void TryParseBounds_ParsesValidQuads(string output, int x, int y, int w, int h)
    {
        TerminalTiling.TryParseBounds(output)
            .Should().Be(new TerminalTiling.WindowRect(x, y, w, h));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0, 0, 1200")]            // too few
    [InlineData("0, 0, 1200, 800, 9")]    // too many
    [InlineData("a, b, c, d")]            // non-numeric
    [InlineData("0, 0, 0, 800")]          // zero width — not a real window
    [InlineData("0, 0, 1200, -5")]        // negative height
    public void TryParseBounds_RejectsMalformedOrDegenerate(string? output)
    {
        TerminalTiling.TryParseBounds(output).Should().BeNull();
    }

    [Fact]
    public void ComputeTerminalRect_RightDock_TerminalTakesLeftSlice()
    {
        // Browser 390 wide on the right of a 1600x1000 work area → terminal hugs the left,
        // full height, taking the remaining 1210.
        var rect = TerminalTiling.ComputeTerminalRect(0, 0, 1600, 1000, 390, DockSide.Right);

        rect.Should().Be(new TerminalTiling.WindowRect(0, 0, 1210, 1000));
    }

    [Fact]
    public void ComputeTerminalRect_LeftDock_TerminalTakesRightSlice()
    {
        // Browser 390 wide on the left → terminal sits to its right.
        var rect = TerminalTiling.ComputeTerminalRect(0, 0, 1600, 1000, 390, DockSide.Left);

        rect.Should().Be(new TerminalTiling.WindowRect(390, 0, 1210, 1000));
    }

    [Fact]
    public void ComputeTerminalRect_RespectsWorkAreaOrigin_ForSecondaryDisplay()
    {
        // A display whose work area starts at (1920, 25): the terminal tile is offset onto it.
        var rect = TerminalTiling.ComputeTerminalRect(1920, 25, 1440, 900, 390, DockSide.Right);

        rect.Should().Be(new TerminalTiling.WindowRect(1920, 25, 1050, 900));
    }

    [Theory]
    [InlineData(1600, 1600)] // browser as wide as the screen → nothing left for the terminal
    [InlineData(1600, 2000)] // browser wider than the screen
    public void ComputeTerminalRect_NoRoomLeft_ReturnsNull(int availWidth, int browserWidth)
    {
        TerminalTiling.ComputeTerminalRect(0, 0, availWidth, 1000, browserWidth, DockSide.Right)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1600, 0)]
    public void ComputeTerminalRect_DegenerateWorkArea_ReturnsNull(int availWidth, int availHeight)
    {
        TerminalTiling.ComputeTerminalRect(0, 0, availWidth, availHeight, 390, DockSide.Right)
            .Should().BeNull();
    }
}
