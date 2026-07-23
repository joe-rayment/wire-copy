// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Single source of truth for the launcher / story-list card grid shape
/// (workspace-ehon, workspace-21uy). The grid is ALWAYS exactly
/// <see cref="FixedColumns"/> columns wide. workspace-ehon briefly made the
/// column count responsive to window width (1–5 columns from a ~52-char target
/// tile), but the user rejected both extremes it produced: 3–5 skinny columns
/// on a wide desktop window, and a collapse to 1 column when the docked
/// browser sidecar narrows the terminal. Tiles are made large and readable by
/// growing each cell's height and text, never by changing the column count.
/// </summary>
internal static class ResponsiveGrid
{
    /// <summary>
    /// The column count of every tile grid, at every window width. Each column
    /// takes half the inner width; on a narrow docked terminal the cells get
    /// narrower, never fewer.
    /// </summary>
    internal const int FixedColumns = 2;

    /// <summary>
    /// Target number of tile rows on screen: with the grid as the only element,
    /// 2 columns × 4 rows ≈ 8 large tiles fill it (workspace-21uy).
    /// </summary>
    internal const int TargetRows = 4;

    /// <summary>
    /// Number of columns for the given inner content width (terminal width minus
    /// borders). Always <see cref="FixedColumns"/> — see the class remarks.
    /// </summary>
    internal static int ColumnsFor(int innerWidth)
    {
        return FixedColumns;
    }

    /// <summary>
    /// Cell height for the given content-area height: tiles grow to fill the
    /// screen at <see cref="TargetRows"/> rows, never shrinking below
    /// <paramref name="floorHeight"/> (the pre-21uy fixed card height) — short
    /// windows keep readable cards and scroll instead. Shared by the launcher
    /// and story-list grids so the two views keep one card proportion.
    /// </summary>
    internal static int CellHeightFor(int availableHeight, int floorHeight)
    {
        return System.Math.Max(floorHeight, availableHeight / TargetRows);
    }

    /// <summary>
    /// Width (in cells) of one card cell given the inner width and column count.
    /// Reserves one cell per inter-column divider. The LAST column takes the
    /// remainder (<see cref="LastCellWidthFor"/>) so rounding never leaves a
    /// ragged right edge.
    /// </summary>
    internal static int CellWidthFor(int innerWidth, int columns)
    {
        if (columns <= 1)
        {
            return System.Math.Max(1, innerWidth);
        }

        return System.Math.Max(1, (innerWidth - (columns - 1)) / columns);
    }

    /// <summary>
    /// Width (in cells) of the final column: the inner width minus every preceding
    /// cell and the inter-column dividers. Absorbs the integer-division remainder
    /// so the grid's right edge is always flush.
    /// </summary>
    internal static int LastCellWidthFor(int innerWidth, int columns)
    {
        if (columns <= 1)
        {
            return System.Math.Max(1, innerWidth);
        }

        var cell = CellWidthFor(innerWidth, columns);
        var used = (cell * (columns - 1)) + (columns - 1); // preceding cells + dividers
        return System.Math.Max(1, innerWidth - used);
    }
}
