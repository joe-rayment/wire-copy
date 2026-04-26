// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared grid layout calculations for multi-column views.
/// </summary>
internal static class GridLayout
{
    /// <summary>
    /// Standard compact card height (3 lines).
    /// </summary>
    public const int CompactCellHeight = 3;

    /// <summary>
    /// Standard full card height (5 lines).
    /// </summary>
    public const int FullCellHeight = 5;

    /// <summary>
    /// Height threshold below which compact mode is used.
    /// </summary>
    public const int CompactThreshold = 15;

    /// <summary>
    /// Calculates the number of columns based on available width.
    /// </summary>
    /// <param name="width">Available terminal width.</param>
    /// <param name="minWidthForTwoColumns">Minimum width to enable 2 columns.</param>
    /// <returns>Column count (1 or 2).</returns>
    public static int GetColumnCount(int width, int minWidthForTwoColumns = 50)
    {
        return width >= minWidthForTwoColumns ? 2 : 1;
    }

    /// <summary>
    /// Calculates cell width for the given column count.
    /// </summary>
    public static int GetCellWidth(int totalWidth, int columns)
    {
        return columns > 1 ? totalWidth / columns : totalWidth;
    }

    /// <summary>
    /// Determines cell height based on available vertical space.
    /// </summary>
    public static int GetCellHeight(int availableHeight)
    {
        return availableHeight < CompactThreshold ? CompactCellHeight : FullCellHeight;
    }

    /// <summary>
    /// Calculates visible rows that fit in the available height.
    /// </summary>
    public static int GetVisibleRows(int availableHeight, int cellHeight, int columns)
    {
        if (cellHeight <= 0 || columns <= 0)
        {
            return 0;
        }

        return Math.Max(1, availableHeight / cellHeight);
    }
}
