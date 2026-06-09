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

    /// <summary>Floor on the app's render width while docked, so it never shrinks to an unusable sliver.</summary>
    internal const int MinDockedRenderWidth = 24;

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
    /// The column the app should START at and the COLUMNS it should draw within while the
    /// browser is docked over part of the terminal (workspace-8fkv). The app can't move the
    /// terminal, so it renders inside the uncovered columns and lets each line's leading
    /// cursor-shift plus a full-width <c>\x1b[K</c> blank the columns the browser sits over —
    /// yielding a true side-by-side view instead of the page covering content.
    ///
    /// <para>
    /// The app always gets the complement of the browser's <paramref name="fraction"/>, minus
    /// a one-column seam gutter, floored at <see cref="MinDockedRenderWidth"/> — the SAME width
    /// on either side. Only the start column differs: right-dock keeps content flush-left
    /// (<c>Offset == 0</c>; the browser covers the right), while left-dock pushes content
    /// flush-right (<c>Offset == fullWidth - Width</c>; the browser covers the blanked left).
    /// Assumes the terminal spans ~the full screen width (the same assumption the pixel
    /// geometry makes), so the column fraction tracks the screen fraction;
    /// <paramref name="fraction"/> is the user-tunable knob, clamped to
    /// [<see cref="MinFraction"/>, <see cref="MaxFraction"/>].
    /// </para>
    /// </summary>
    public static (int Offset, int Width) DockedContentLayout(int fullWidth, DockSide side, double fraction)
    {
        if (fullWidth <= 0)
        {
            return (0, fullWidth);
        }

        var appFraction = 1.0 - Math.Clamp(fraction, MinFraction, MaxFraction);
        var appWidth = (int)Math.Floor(fullWidth * appFraction) - 1; // -1: a seam gutter at the dock edge
        appWidth = Math.Clamp(appWidth, Math.Min(MinDockedRenderWidth, fullWidth), fullWidth);

        var offset = side == DockSide.Left ? fullWidth - appWidth : 0;
        return (offset, appWidth);
    }
}
