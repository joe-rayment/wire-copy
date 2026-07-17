// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Single source of truth for the launcher / story-list card grid proportion
/// (workspace-ehon). Both grids previously hardcoded a maximum of 2 columns,
/// tuned for an ~80–120-column terminal. In the desktop shell the window is much
/// wider (~140–166 columns), so 2 columns stretched into long, thin "skinny and
/// long" ribbons with acres of dead space.
///
/// The fix derives the column count from a target tile width so each tile keeps
/// the readable ~<see cref="TargetTileWidth"/>-char proportion at every window
/// size, and the grid grows to more columns to FILL the width instead of
/// stretching two. On the default desktop window this yields 3 columns (the old
/// proportion); ultra-wide → 4; a narrow terminal → 2 then 1.
/// </summary>
internal static class ResponsiveGrid
{
    /// <summary>
    /// Target width (in terminal cells) of a single card. ~52 keeps the readable
    /// proportion the user liked from the terminal build. This is a knob: the
    /// Definition of Done is how the grid LOOKS at the default window, not the
    /// exact number.
    /// </summary>
    internal const int TargetTileWidth = 52;

    /// <summary>
    /// Hard ceiling on the column count. Only bites past ~<c>MaxColumns *
    /// TargetTileWidth</c> cells (~260), preventing pathologically tiny tiles on
    /// giant/ultra-wide displays while still letting the grid fill normal windows.
    /// </summary>
    internal const int MaxColumns = 5;

    /// <summary>
    /// Number of columns for the given inner content width (terminal width minus
    /// borders). Rounds to the nearest whole number of <see cref="TargetTileWidth"/>
    /// tiles so a window sits at the column count whose tiles land closest to the
    /// target — e.g. ~90 → 2, ~160 → 3, ~210 → 4 — clamped to [1, <see cref="MaxColumns"/>].
    /// </summary>
    internal static int ColumnsFor(int innerWidth)
    {
        if (innerWidth <= 0)
        {
            return 1;
        }

        var raw = (int)System.Math.Round(innerWidth / (double)TargetTileWidth, System.MidpointRounding.AwayFromZero);
        return System.Math.Clamp(raw, 1, MaxColumns);
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
