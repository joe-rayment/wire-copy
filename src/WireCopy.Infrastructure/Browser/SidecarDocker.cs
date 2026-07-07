// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Drives the docked-window placement (workspace-75ng): anchor on-screen, read a STABLE work
/// area (defeating the phantom read from a just-un-parked window), place flush, then read the
/// CLAMPED width back and re-place so the right edge is flush and the window is always fully
/// on-screen. Pure orchestration over <see cref="IDockWindowGeometry"/> — the entire decision
/// is reproducible in unit tests.
/// </summary>
internal static class SidecarDocker
{
    /// <summary>Max work-area reads while waiting for the value to stabilize after the anchor move.</summary>
    internal const int MaxStabilizeReads = 6;

    /// <summary>
    /// Places the docked window and returns the display work area plus the FINAL applied browser
    /// rect (the caller tiles the terminal into the complement), or null if no plausible display
    /// could be read (placement skipped rather than guessed).
    /// </summary>
    internal static async Task<(SidecarGeometry.DisplayInfo Display, TerminalTiling.WindowRect Browser)?> PlaceAsync(
        IDockWindowGeometry geo,
        DockSide side,
        int requestedWidthPx,
        double dockFraction,
        ILogger? logger = null,
        (int X, int Y)? anchor = null)
    {
        await geo.NormalizeAsync().ConfigureAwait(false);

        // Anchor the window so its work-area read resolves to a REAL display (a window still
        // parked off-screen reports a phantom/empty work area → far-left/full-width placement).
        // workspace-9k27.8: anchor at the TERMINAL's position when known — the global origin
        // is always the PRIMARY display, which yanked the dock (and the terminal tile) onto
        // the wrong monitor whenever the terminal lived on a secondary display.
        var (anchorX, anchorY) = anchor ?? (0, 0);
        await geo.MoveAsync(anchorX, anchorY).ConfigureAwait(false);

        var display = await ReadStableDisplayAsync(geo).ConfigureAwait(false);
        if (display is null)
        {
            logger?.LogWarning(
                "Sidecar dock skipped: could not read a plausible display work area (window may still be "
                + "settling on-screen). The browser was left where it is rather than placed off-screen.");
            return null;
        }

        // First placement at the requested (phone) width.
        var planned = SidecarGeometry.PlanDockedWindow(display.Value, side, requestedWidthPx, dockFraction, actualWidth: null);
        await geo.SetWindowAsync(planned).ConfigureAwait(false);

        // Chromium clamps the requested width up to its platform minimum, so re-read the ACTUAL
        // width and re-place flush — otherwise the right edge spills past the Dock (the user's bug).
        var actual = await geo.ReadWindowAsync().ConfigureAwait(false);
        var finalRect = SidecarGeometry.PlanDockedWindow(
            display.Value, side, requestedWidthPx, dockFraction, actualWidth: actual?.Width);
        await geo.SetWindowAsync(finalRect).ConfigureAwait(false);

        var summary =
            $"left={finalRect.X} top={finalRect.Y} {finalRect.Width}x{finalRect.Height} in work area "
            + $"{display.Value.AvailLeft},{display.Value.AvailTop} {display.Value.AvailWidth}x{display.Value.AvailHeight} "
            + $"(requested {requestedWidthPx}, actual {actual?.Width}, side {side})";
        logger?.LogInformation("Sidecar placed: {Summary}", summary);

        return (display.Value, finalRect);
    }

    /// <summary>
    /// Reads the work area repeatedly (with a settle between) until two consecutive reads agree
    /// and are plausible — the just-anchored window needs a beat to re-associate with the real
    /// display, and a single early read can return phantom off-screen geometry. Returns the
    /// stable value, or the last plausible one, or null if none was ever plausible.
    /// (Internal so <see cref="CornerParker"/> reuses the same stabilized read — workspace-v7g7.)
    /// </summary>
    internal static async Task<SidecarGeometry.DisplayInfo?> ReadStableDisplayAsync(IDockWindowGeometry geo)
    {
        SidecarGeometry.DisplayInfo? lastPlausible = null;
        SidecarGeometry.DisplayInfo? prev = null;

        for (var i = 0; i < MaxStabilizeReads; i++)
        {
            var read = await geo.ReadDisplayAsync().ConfigureAwait(false);
            if (read is { IsPlausible: true })
            {
                lastPlausible = read;
                if (prev is { } p && p.Equals(read.Value))
                {
                    return read; // stable + plausible
                }

                prev = read;
            }
            else
            {
                prev = null; // an implausible read breaks the stability streak
            }

            await geo.SettleAsync().ConfigureAwait(false);
        }

        return lastPlausible;
    }
}
