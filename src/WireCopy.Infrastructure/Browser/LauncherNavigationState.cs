// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Manages launcher-specific navigation state (selected index, scroll offset).
/// Extracted from NavigationService to separate launcher concerns from core navigation.
/// </summary>
public class LauncherNavigationState
{
    private readonly ILogger _logger;

    private int _launcherSelectedIndex;
    private int _launcherScrollOffset;

    public LauncherNavigationState(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the selected index on the launcher grid.
    /// </summary>
    /// <remarks>
    /// Sentinel values: -1 = URL bar, 0+ = grid item.
    /// </remarks>
    public int SelectedIndex
    {
        get => _launcherSelectedIndex;
        set => _launcherSelectedIndex = Math.Max(-1, value);
    }

    /// <summary>
    /// Gets or sets the scroll offset for the launcher grid.
    /// </summary>
    public int ScrollOffset
    {
        get => _launcherScrollOffset;
        set => _launcherScrollOffset = Math.Max(0, value);
    }

    /// <summary>
    /// Computes a new index for 2D grid navigation.
    /// </summary>
    /// <param name="currentIndex">Current selected index.</param>
    /// <param name="totalItems">Total number of items in the grid.</param>
    /// <param name="direction">Direction: 0=up, 1=down, 2=left, 3=right.</param>
    /// <param name="columns">Number of columns in the grid.</param>
    /// <returns>New index, clamped to [0, totalItems-1].</returns>
    public static int MoveInGrid(int currentIndex, int totalItems, int direction, int columns = 2)
    {
        if (totalItems <= 0)
        {
            return 0;
        }

        var row = currentIndex / columns;
        var col = currentIndex % columns;

        switch (direction)
        {
            case 0: // Up
                row = Math.Max(0, row - 1);
                break;
            case 1: // Down
                row++;
                break;
            case 2: // Left
                col = Math.Max(0, col - 1);
                break;
            case 3: // Right
                col = Math.Min(columns - 1, col + 1);
                break;
        }

        var newIndex = (row * columns) + col;
        return Math.Clamp(newIndex, 0, totalItems - 1);
    }

    /// <summary>
    /// Enters launcher mode, resetting selection and scroll.
    /// </summary>
    public void Enter()
    {
        _launcherSelectedIndex = 0;
        _launcherScrollOffset = 0;
        _logger.LogDebug("Entered launcher mode");
    }
}
