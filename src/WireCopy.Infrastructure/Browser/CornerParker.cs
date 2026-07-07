// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Places the PARKED browser window as a deliberate tile flush in the work area's bottom-right
/// corner (workspace-v7g7, <see cref="Configuration.ParkMode.Corner"/>). For platforms that clamp
/// off-screen coordinates back on-screen (macOS), the old off-screen park degraded into a stray
/// clamped sliver that "looks like a mistake" — a corner tile looks intentional, keeps painting,
/// and never touches focus or window state. Pure orchestration over
/// <see cref="IDockWindowGeometry"/> (same seam as <see cref="SidecarDocker"/>) so every scenario
/// — clamping OS, DPR skew, user resize — is reproducible in unit tests.
/// </summary>
internal static class CornerParker
{
    /// <summary>Applied-position slack before the corrective nudge fires (sub-pixel/rounding).</summary>
    internal const int PositionTolerance = 2;

    /// <summary>
    /// Ensures the window sits in the corner. Returns the rect to remember as "where the tile
    /// is" plus whether that came from a SETTLED observation, or null when no plausible display
    /// could be read (the window is left where it is).
    ///
    /// <para>
    /// NEVER-FIGHT-THE-USER (the field bug this fixes): once a park has observed the tile where
    /// we left it (<paramref name="respectDrift"/>, armed by the caller after a settled
    /// observation), a later drift is the USER's drag/resize and is adopted rather than snapped
    /// back on the next background park (prefetch parks run twice per article). BEFORE that
    /// first settled observation a drift is launch-time WM interference (openbox re-places a
    /// freshly mapped window over the early park) and is corrected, not adopted. Callers reset
    /// their remembered rect at on-screen transitions (restore/dock). The caller must also
    /// ensure the window is not minimized (a minimized window is already hidden and must be
    /// left alone).
    /// </para>
    /// </summary>
    internal static async Task<CornerPlacement?> PlaceAsync(
        IDockWindowGeometry geo,
        int requestedWidth,
        int requestedHeight,
        int margin,
        TerminalTiling.WindowRect? lastApplied,
        bool respectDrift,
        SidecarGeometry.DisplayInfo? displayOverride = null,
        ILogger? logger = null)
    {
        var trace = new System.Text.StringBuilder();
        var current = await geo.ReadWindowAsync().ConfigureAwait(false);
        trace.Append($"entry={Fmt(current)}");
        if (lastApplied is { } prev && current is { } cur)
        {
            var near = Math.Abs(cur.X - prev.X) <= PositionTolerance
                && Math.Abs(cur.Y - prev.Y) <= PositionTolerance
                && cur.Width == prev.Width
                && cur.Height == prev.Height;
            if (near)
            {
                return new CornerPlacement(cur, Settled: true);
            }

            if (respectDrift)
            {
                logger?.LogDebug(
                    "Corner park: window moved/resized since we placed it ({Prev} -> {Cur}) — respecting the user's placement",
                    prev,
                    cur);
                return new CornerPlacement(cur, Settled: true);
            }

            // The tile was never observed settled — this drift is WM/launch interference, not
            // the user's hand. Fall through and re-place.
        }

        // Prefer the display captured at launch BEFORE viewport emulation (workspace-v7g7): the
        // emulated fetch page's JS screen metrics report the 1280x720 viewport, not the display.
        // Fall back to reading through the window — anchoring it on-screen once if the read is
        // phantom (a window spawned far off-screen reports no plausible work area, the dock's
        // known trap). On clamping platforms (macOS) the window is always somehow on-screen, so
        // the first read usually succeeds and no anchor flash ever happens.
        var display = displayOverride is { IsPlausible: true } ? displayOverride : null;
        if (display is null)
        {
            display = await SidecarDocker.ReadStableDisplayAsync(geo).ConfigureAwait(false);
            if (display is null)
            {
                await geo.MoveAsync(0, 0).ConfigureAwait(false);
                display = await SidecarDocker.ReadStableDisplayAsync(geo).ConfigureAwait(false);
            }
        }

        if (display is null)
        {
            logger?.LogWarning(
                "Corner park skipped: could not read a plausible display work area; the browser window "
                + "was left where it is.");
            return null;
        }

        trace.Append($" display={display.Value.AvailLeft},{display.Value.AvailTop} {display.Value.AvailWidth}x{display.Value.AvailHeight}");

        var planned = SidecarGeometry.PlanCornerWindow(display.Value, requestedWidth, requestedHeight, margin);
        trace.Append($" planned={Fmt(planned)}");
        await geo.SetWindowAsync(planned).ConfigureAwait(false);
        await geo.SettleAsync().ConfigureAwait(false);

        // Chromium clamps tiny sizes up to its platform minimum — re-plan flush with the REAL
        // size so the corner stays flush (same read-back pattern as the dock).
        var actual = await geo.ReadWindowAsync().ConfigureAwait(false);
        trace.Append($" postSet={Fmt(actual)}");
        if (actual is { } sized && (sized.Width != planned.Width || sized.Height != planned.Height))
        {
            planned = SidecarGeometry.PlanCornerWindow(
                display.Value, requestedWidth, requestedHeight, margin, (sized.Width, sized.Height));
            trace.Append($" replanned={Fmt(planned)}");
            await geo.SetWindowAsync(planned).ConfigureAwait(false);
            await geo.SettleAsync().ConfigureAwait(false);
            actual = await geo.ReadWindowAsync().ConfigureAwait(false);
            trace.Append($" postReplan={Fmt(actual)}");
        }

        // One corrective nudge when the OS applied the position with an offset (frame insets,
        // clamp deltas): ask for the mirror point so the applied result lands on the plan. If the
        // OS is hard-clamping (our plan overlapped a dock/menu bar), the re-read simply keeps the
        // OS's flush answer — never loop.
        if (actual is { } shifted
            && (Math.Abs(shifted.X - planned.X) > PositionTolerance || Math.Abs(shifted.Y - planned.Y) > PositionTolerance))
        {
            var nudged = new TerminalTiling.WindowRect(
                planned.X - (shifted.X - planned.X),
                planned.Y - (shifted.Y - planned.Y),
                planned.Width,
                planned.Height);
            trace.Append($" nudged={Fmt(nudged)}");
            await geo.SetWindowAsync(nudged).ConfigureAwait(false);
            await geo.SettleAsync().ConfigureAwait(false);
            actual = await geo.ReadWindowAsync().ConfigureAwait(false);
            trace.Append($" postNudge={Fmt(actual)}");
        }

        var final = actual ?? planned;
        var summary =
            $"left={final.X} top={final.Y} {final.Width}x{final.Height} in work area "
            + $"{display.Value.AvailLeft},{display.Value.AvailTop} "
            + $"{display.Value.AvailWidth}x{display.Value.AvailHeight} [{trace}]";
        logger?.LogInformation("Corner park placed: {Summary}", summary);
        return new CornerPlacement(final, Settled: false);
    }

    private static string Fmt(TerminalTiling.WindowRect? r) =>
        r is { } v ? $"({v.X},{v.Y} {v.Width}x{v.Height})" : "(null)";

    /// <param name="Rect">Where the tile is now — the caller's remembered rect.</param>
    /// <param name="Settled">True when the window was OBSERVED at the remembered rect (or a
    /// respected user drift) — arms the caller's respect-drift flag; false right after a full
    /// placement, so the next park confirms it before drifts count as the user's.</param>
    internal readonly record struct CornerPlacement(TerminalTiling.WindowRect Rect, bool Settled);
}
