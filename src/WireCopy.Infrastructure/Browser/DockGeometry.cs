// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Pure geometry for the docked browser window (workspace-v7mb). Computes the CDP
/// window bounds from the screen size, configured side, and split fraction. Kept
/// separate from <see cref="BrowserSession"/> so the math is unit-testable without
/// a live browser — the live CDP path itself is covered by the Xvfb integration test.
/// </summary>
internal static class DockGeometry
{
    /// <summary>Lower clamp on the dock fraction to avoid a degenerate sliver window.</summary>
    internal const double MinFraction = 0.2;

    /// <summary>Upper clamp on the dock fraction so the terminal always keeps some screen.</summary>
    internal const double MaxFraction = 0.8;

    /// <summary>
    /// Computes the docked window bounds. <paramref name="fraction"/> is clamped to
    /// [<see cref="MinFraction"/>, <see cref="MaxFraction"/>]. The window spans the full
    /// screen height and is pinned to the left (<see cref="DockSide.Left"/>) or right
    /// (<see cref="DockSide.Right"/>) edge. Non-positive screen dimensions fall back to
    /// 1280x800 so a failed <c>screen.avail*</c> read still yields a usable window.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) Compute(
        int screenWidth, int screenHeight, DockSide side, double fraction)
    {
        var safeWidth = screenWidth > 0 ? screenWidth : 1280;
        var safeHeight = screenHeight > 0 ? screenHeight : 800;
        var clamped = Math.Clamp(fraction, MinFraction, MaxFraction);
        var width = (int)Math.Round(safeWidth * clamped, MidpointRounding.AwayFromZero);
        width = Math.Clamp(width, 1, safeWidth);
        var left = side == DockSide.Left ? 0 : safeWidth - width;
        return (left, 0, width, safeHeight);
    }
}
