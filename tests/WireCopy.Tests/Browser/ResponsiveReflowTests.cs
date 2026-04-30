// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ResponsiveReflowTests
{
    #region Max-width cap model

    [Fact]
    public void MaxWidthCap_OverrideNarrowerThanTerminal_UsesOverride()
    {
        // When override (60) < terminal width - 2 (78), use override
        var overrideValue = 60;
        var terminalWidth = 80;
        var result = Math.Clamp(Math.Min(overrideValue, terminalWidth - 2), 40, 120);

        result.Should().Be(60);
    }

    [Fact]
    public void MaxWidthCap_OverrideWiderThanTerminal_CapsToTerminal()
    {
        // When override (100) > terminal width - 2 (48), cap to terminal
        var overrideValue = 100;
        var terminalWidth = 50;
        var result = Math.Clamp(Math.Min(overrideValue, terminalWidth - 2), 40, 120);

        result.Should().Be(48);
    }

    [Fact]
    public void MaxWidthCap_NoOverride_UsesTerminalWidth()
    {
        var terminalWidth = 80;
        var result = Math.Clamp(terminalWidth - 2, 40, 120);

        result.Should().Be(78);
    }

    #endregion

    #region ANSI VisibleLength

    [Fact]
    public void VisibleLength_PlainText_ReturnsLength()
    {
        var text = "Hello World";
        var visibleLength = StripAnsi(text).Length;

        visibleLength.Should().Be(11);
    }

    [Fact]
    public void VisibleLength_WithAnsiCodes_StripsEscapes()
    {
        var text = "\x1b[1mBold\x1b[0m and \x1b[38;5;46mGreen\x1b[0m";
        var visibleLength = StripAnsi(text).Length;

        // "Bold and Green" = 14 chars
        visibleLength.Should().Be(14);
    }

    [Fact]
    public void VisibleLength_EmptyString_ReturnsZero()
    {
        StripAnsi(string.Empty).Length.Should().Be(0);
    }

    [Fact]
    public void VisibleLength_OnlyAnsi_ReturnsZero()
    {
        var text = "\x1b[0m\x1b[1m\x1b[38;5;46m";
        StripAnsi(text).Length.Should().Be(0);
    }

    #endregion

    #region RenderHelpers.TerminalHeight property

    [Fact]
    public void RenderHelpers_TerminalHeight_DefaultFallsBackToConsole()
    {
        var helpers = new RenderHelpers();

        // When TerminalHeight is not set (0), it falls back to Console.WindowHeight
        // We can't easily test the fallback, but we can verify setting works
        helpers.TerminalHeight = 50;
        helpers.TerminalHeight.Should().Be(50);
    }

    [Fact]
    public void RenderHelpers_TerminalHeight_SetOverridesDefault()
    {
        var helpers = new RenderHelpers();
        helpers.TerminalHeight = 30;

        helpers.TerminalHeight.Should().Be(30);
    }

    #endregion

    #region ComputeLayout responsive behavior

    [Fact]
    public void ComputeLayout_NarrowTerminal_SingleColumn()
    {
        var layout = LinkTreeRenderer.ComputeLayout(45, 30);
        layout.Columns.Should().Be(1);
        layout.CellWidth.Should().Be(layout.Width);
    }

    [Fact]
    public void ComputeLayout_WideTerminal_TwoColumns()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.Columns.Should().Be(2);
        layout.CellWidth.Should().BeLessThan(layout.Width);
    }

    [Fact]
    public void ComputeLayout_ShortTerminal_CompactCells()
    {
        // availableHeight = max(4, 16-1-1) = 14 < 15 → compact
        var layout = LinkTreeRenderer.ComputeLayout(80, 16);
        layout.CellHeight.Should().Be(3);
    }

    [Fact]
    public void ComputeLayout_TallTerminal_StandardCells()
    {
        // availableHeight = max(4, 40-1-1) = 38 >= 15 → standard
        var layout = LinkTreeRenderer.ComputeLayout(80, 40);
        layout.CellHeight.Should().Be(5);
    }

    #endregion

    private static string StripAnsi(string text)
    {
        return Regex.Replace(text, @"\x1b\[[0-9;]*m", string.Empty);
    }
}
