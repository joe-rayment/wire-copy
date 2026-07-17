// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// The shared launcher / story-list column formula (workspace-ehon). The Definition
/// of Done is how the grid LOOKS at the default window, so these lock the maths that
/// the visual verification then confirms: column count tracks the target tile width,
/// and the cells + dividers always fill the width exactly (the last cell absorbs the
/// remainder — no ragged right edge, no dead-space ribbon).
/// </summary>
[Trait("Category", "Unit")]
public class ResponsiveGridTests
{
    [Theory]
    [InlineData(38, 1)]   // very narrow → single column
    [InlineData(88, 2)]   // ~90-col terminal
    [InlineData(158, 3)]  // default desktop-shell window — the old proportion, now 3-up
    [InlineData(208, 4)]  // ultra-wide
    [InlineData(1000, 5)] // clamped to MaxColumns
    public void ColumnsFor_TracksTargetTileWidth(int innerWidth, int expected)
    {
        ResponsiveGrid.ColumnsFor(innerWidth).Should().Be(expected);
    }

    [Fact]
    public void ColumnsFor_NonPositiveWidth_IsOne()
    {
        ResponsiveGrid.ColumnsFor(0).Should().Be(1);
        ResponsiveGrid.ColumnsFor(-5).Should().Be(1);
    }

    [Theory]
    [InlineData(158, 3)]
    [InlineData(160, 3)]
    [InlineData(208, 4)]
    [InlineData(93, 2)]
    [InlineData(100, 1)]
    public void CellWidths_PlusDividers_ExactlyFillInnerWidth(int innerWidth, int columns)
    {
        var cell = ResponsiveGrid.CellWidthFor(innerWidth, columns);
        var last = ResponsiveGrid.LastCellWidthFor(innerWidth, columns);

        // (columns-1) base cells + the remainder-absorbing last cell + (columns-1) dividers.
        var total = (cell * (columns - 1)) + last + (columns - 1);
        total.Should().Be(innerWidth, "the grid's right edge must be flush — the last cell absorbs the remainder");
        last.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void SingleColumn_CellIsFullWidth()
    {
        ResponsiveGrid.CellWidthFor(80, 1).Should().Be(80);
        ResponsiveGrid.LastCellWidthFor(80, 1).Should().Be(80);
    }

    [Fact]
    public void LastCell_NeverAnemic_RemainderSpreadIsSmall()
    {
        // Guards against a ragged final column: the last tile must stay within
        // (columns-1) cells of the base width across every window size.
        for (var w = 60; w <= 260; w += 7)
        {
            var cols = ResponsiveGrid.ColumnsFor(w);
            if (cols < 2)
            {
                continue;
            }

            var cell = ResponsiveGrid.CellWidthFor(w, cols);
            var last = ResponsiveGrid.LastCellWidthFor(w, cols);
            System.Math.Abs(last - cell).Should().BeLessThanOrEqualTo(cols - 1,
                $"width {w}, {cols} cols: the last tile must not be a ragged remainder");
        }
    }
}
