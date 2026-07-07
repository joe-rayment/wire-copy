// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Pure geometry for placing the docked sidecar window and the tiled terminal (workspace-75ng).
/// Split out from <see cref="BrowserSession"/> so the WHOLE placement decision is unit-testable
/// in any environment — the bugs the user hit (window past the Dock, full-width, off-screen)
/// were placement-math bugs that an Xvfb integration test could not reproduce (no Retina, no
/// Dock, no menu bar). <see cref="SidecarDocker"/> drives the imperative read/apply steps
/// through a seam so the same scenarios can be simulated in tests.
/// </summary>
internal static class SidecarGeometry
{
    /// <summary>Smallest plausible work-area dimension; anything below is treated as a bad read.</summary>
    internal const int MinPlausibleDimension = 200;

    /// <summary>
    /// Computes where the docked browser window should go: full work-area height, pinned FLUSH
    /// to the configured edge, and ALWAYS fully inside the work area (so it can never spill over
    /// the Dock/menu bar or off-screen).
    ///
    /// <para>
    /// Width rules (workspace-75ng): the window is a phone-shaped sidecar, so the width is
    /// CAPPED at <see cref="DockGeometry.MaxFraction"/> of the work area — it must NEVER fill
    /// the screen, which is the "full-width" bug the user hit when a large read-back width was
    /// trusted verbatim. <paramref name="actualWidth"/> is the window's real width after
    /// Chromium clamps the requested phone width UP to its platform minimum; when known
    /// (post-readback) it is used so the right edge lands flush even though the window is wider
    /// than requested, but it is still capped. <paramref name="requestedWidthPx"/> &lt;= 0 falls
    /// back to <paramref name="dockFraction"/> of the work area.
    /// </para>
    /// </summary>
    internal static TerminalTiling.WindowRect PlanDockedWindow(
        DisplayInfo display, DockSide side, int requestedWidthPx, double dockFraction, int? actualWidth)
    {
        var maxWidth = (int)Math.Round(display.AvailWidth * DockGeometry.MaxFraction, MidpointRounding.AwayFromZero);
        maxWidth = Math.Clamp(maxWidth, 1, display.AvailWidth);
        var minWidth = Math.Min(280, maxWidth);

        int desired;
        if (actualWidth is > 0)
        {
            desired = actualWidth.Value;
        }
        else if (requestedWidthPx > 0)
        {
            desired = requestedWidthPx;
        }
        else
        {
            var fraction = Math.Clamp(dockFraction, DockGeometry.MinFraction, DockGeometry.MaxFraction);
            desired = (int)Math.Round(display.AvailWidth * fraction, MidpointRounding.AwayFromZero);
        }

        // Cap at MaxFraction of the work area so the sidecar is never full-width, and floor it so
        // it is never a degenerate sliver.
        var width = Math.Clamp(desired, minWidth, maxWidth);
        var height = Math.Clamp(display.AvailHeight, 1, display.AvailHeight);

        // Flush to the chosen edge, then HARD-clamp the left so the whole window is inside the
        // work area regardless of width (this is what makes "off-screen" impossible).
        var left = side == DockSide.Left
            ? display.AvailLeft
            : display.AvailLeft + display.AvailWidth - width;
        left = Math.Clamp(left, display.AvailLeft, display.AvailLeft + display.AvailWidth - width);

        return new TerminalTiling.WindowRect(left, display.AvailTop, width, height);
    }

    /// <summary>
    /// Computes the parked corner tile (workspace-v7g7): the requested size, shrunk if the work
    /// area (minus margins) is smaller, placed FLUSH in the bottom-right corner with
    /// <paramref name="margin"/> to the edges, and hard-clamped fully inside the work area.
    /// <paramref name="actualSize"/> is the window's real size after Chromium clamps the request
    /// to its platform minimum (post-readback); when known it is used so the tile still sits
    /// flush even though the window is bigger than asked — same pattern as
    /// <see cref="PlanDockedWindow"/>'s <c>actualWidth</c>.
    /// </summary>
    internal static TerminalTiling.WindowRect PlanCornerWindow(
        DisplayInfo display,
        int requestedWidth,
        int requestedHeight,
        int margin,
        (int Width, int Height)? actualSize = null)
    {
        margin = Math.Max(0, margin);
        var maxWidth = Math.Max(1, display.AvailWidth - (2 * margin));
        var maxHeight = Math.Max(1, display.AvailHeight - (2 * margin));

        var width = Math.Clamp(actualSize?.Width ?? requestedWidth, 1, maxWidth);
        var height = Math.Clamp(actualSize?.Height ?? requestedHeight, 1, maxHeight);

        var left = display.AvailLeft + display.AvailWidth - width - margin;
        var top = display.AvailTop + display.AvailHeight - height - margin;

        // Hard-clamp inside the work area regardless of size (off-screen impossible).
        left = Math.Max(left, display.AvailLeft);
        top = Math.Max(top, display.AvailTop);

        return new TerminalTiling.WindowRect(left, top, width, height);
    }

    /// <summary>
    /// Computes the terminal's tile (the work-area slice the docked browser leaves free), given
    /// the browser's FINAL placed rect. Delegates to <see cref="TerminalTiling.ComputeTerminalRect"/>.
    /// </summary>
    internal static TerminalTiling.WindowRect? PlanTerminalTile(
        DisplayInfo display, DockSide side, int browserWidth) =>
        TerminalTiling.ComputeTerminalRect(
            display.AvailLeft, display.AvailTop, display.AvailWidth, display.AvailHeight, browserWidth, side);

    /// <summary>
    /// A display's WORK AREA (visible frame) in CDP/screen logical pixels: the region left
    /// after the macOS menu bar and Dock are excluded — the same thing Rectangle snaps to.
    /// <c>window.screen.availLeft/availTop/availWidth/availHeight</c>.
    /// </summary>
    internal readonly record struct DisplayInfo(int AvailLeft, int AvailTop, int AvailWidth, int AvailHeight)
    {
        /// <summary>
        /// True when the read looks like a real display rather than a degenerate/empty value.
        /// A window read while still parked off-screen can report a 0-sized or tiny work area;
        /// those must not drive placement.
        /// </summary>
        public bool IsPlausible =>
            AvailWidth >= MinPlausibleDimension && AvailHeight >= MinPlausibleDimension;
    }
}
