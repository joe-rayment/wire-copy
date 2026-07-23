// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// The shared launcher / story-list column contract (workspace-21uy): every tile
/// grid is exactly 2 columns at every window width — no collapse to 1 when the
/// docked sidecar narrows the terminal, no 3–5 skinny columns on a wide window.
/// The width tests lock the cell maths: cells + dividers always fill the width
/// exactly (the last cell absorbs the remainder — no ragged right edge).
/// </summary>
[Trait("Category", "Unit")]
public class ResponsiveGridTests
{
    [Theory]
    [InlineData(38)]   // sidecar-docked narrow terminal — must NOT collapse to 1
    [InlineData(88)]   // ~90-col terminal
    [InlineData(158)]  // default desktop-shell window
    [InlineData(208)]  // ultra-wide — must NOT fan out to 3+
    [InlineData(1000)]
    public void ColumnsFor_IsAlwaysTwo(int innerWidth)
    {
        ResponsiveGrid.ColumnsFor(innerWidth).Should().Be(2);
    }

    [Fact]
    public void ColumnsFor_NonPositiveWidth_StaysTwo_CellWidthClampsInstead()
    {
        // Degenerate widths clamp at CellWidthFor (>= 1 cell), not the column count.
        ResponsiveGrid.ColumnsFor(0).Should().Be(2);
        ResponsiveGrid.ColumnsFor(-5).Should().Be(2);
        ResponsiveGrid.CellWidthFor(0, 2).Should().BeGreaterThanOrEqualTo(1);
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
