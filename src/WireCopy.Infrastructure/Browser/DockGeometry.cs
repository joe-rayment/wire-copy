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
    /// Computes the docked window bounds for a display whose work area starts at the
    /// virtual-screen origin (0, 0) — i.e. the primary display. Equivalent to calling
    /// <see cref="Compute(int, int, int, int, DockSide, double)"/> with a zero origin.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) Compute(
        int screenWidth, int screenHeight, DockSide side, double fraction) =>
        Compute(screenWidth, screenHeight, 0, 0, side, fraction);

    /// <summary>
    /// Computes the docked window bounds within a specific display's work area
    /// (workspace-nqqs). <paramref name="availLeft"/>/<paramref name="availTop"/> are the
    /// work-area origin of the target display in VIRTUAL-SCREEN coordinates (Chrome's
    /// <c>screen.availLeft</c>/<c>availTop</c>) — the same coordinate space CDP
    /// <c>Browser.setWindowBounds</c> uses. Adding them keeps the dock pinned to the
    /// display the window actually lives on (and clear of the taskbar) instead of
    /// assuming the primary display's origin.
    ///
    /// <para>
    /// <paramref name="fraction"/> is clamped to [<see cref="MinFraction"/>,
    /// <see cref="MaxFraction"/>]. The window spans the display's full work-area height
    /// and is pinned to its left (<see cref="DockSide.Left"/>) or right
    /// (<see cref="DockSide.Right"/>) edge. Non-positive screen dimensions fall back to
    /// 1280x800 so a failed <c>screen.avail*</c> read still yields a usable window.
    /// </para>
    /// </summary>
    public static (int Left, int Top, int Width, int Height) Compute(
        int screenWidth, int screenHeight, int availLeft, int availTop, DockSide side, double fraction)
    {
        var safeWidth = screenWidth > 0 ? screenWidth : 1280;
        var safeHeight = screenHeight > 0 ? screenHeight : 800;
        var clamped = Math.Clamp(fraction, MinFraction, MaxFraction);
        var width = (int)Math.Round(safeWidth * clamped, MidpointRounding.AwayFromZero);
        width = Math.Clamp(width, 1, safeWidth);
        var left = availLeft + (side == DockSide.Left ? 0 : safeWidth - width);
        return (left, availTop, width, safeHeight);
    }

    /// <summary>
    /// Computes docked bounds for a PREFERRED pixel width (workspace-o5yf) — the
    /// phone-shaped sidecar. The width is clamped so it never exceeds the
    /// <see cref="MaxFraction"/> share of the screen (tiny displays fall back
    /// toward the fraction behaviour) and never collapses below a usable floor.
    /// <paramref name="widthPx"/> &lt;= 0 delegates to the fraction overload.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) Compute(
        int screenWidth, int screenHeight, int availLeft, int availTop, DockSide side, double fraction, int widthPx)
    {
        if (widthPx <= 0)
        {
            return Compute(screenWidth, screenHeight, availLeft, availTop, side, fraction);
        }

        var safeWidth = screenWidth > 0 ? screenWidth : 1280;
        var safeHeight = screenHeight > 0 ? screenHeight : 800;
        var maxWidth = (int)Math.Round(safeWidth * MaxFraction, MidpointRounding.AwayFromZero);
        var width = Math.Clamp(widthPx, Math.Min(280, safeWidth), Math.Max(1, maxWidth));
        var left = availLeft + (side == DockSide.Left ? 0 : safeWidth - width);
        return (left, availTop, width, safeHeight);
    }
}
