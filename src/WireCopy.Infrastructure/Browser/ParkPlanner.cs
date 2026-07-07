// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// The PURE decision of what a "park / get out of the way" request does to the headed window
/// (workspace-v7g7), split out of <see cref="BrowserSession"/> so every transition — including
/// the macOS ones — is unit-testable in this environment.
///
/// <para>
/// The field bug this encodes against: the old park unconditionally set
/// <c>windowState=normal</c> before moving, and background prefetch parks twice per article — so
/// a window the USER had minimized (Cmd+M) was deminiaturized (popping Chromium to the
/// foreground) moments after every new site open. Rule one: a minimized window that is not OUR
/// minimize is the user's, and it is NEVER touched.
/// </para>
/// </summary>
internal static class ParkPlanner
{
    internal enum ParkAction
    {
        /// <summary>The window is already invisible (minimized) — do absolutely nothing.</summary>
        LeaveMinimized,

        /// <summary>Return the window to the minimized state the user had it in before an
        /// interaction (captcha/login) summoned it (windowState=minimized).</summary>
        ReMinimize,

        /// <summary>Place the intentional bottom-right corner tile (<see cref="CornerParker"/>).</summary>
        PlaceCorner,

        /// <summary>The classic hide: off-screen move, plus iconify on non-macOS
        /// (<see cref="IDockWindowGeometry.HideAsync"/>).</summary>
        HideOffscreen,
    }

    /// <summary>
    /// Decides the park action from the window's current CDP state.
    /// </summary>
    /// <param name="windowState">CDP windowState ("normal"/"minimized"/"maximized"/"fullscreen");
    /// null (unreadable) is treated as normal — the actions below never deminiaturize, so a wrong
    /// guess cannot pop a user-minimized window back out.</param>
    /// <param name="iconifiedByUs">Whether the session believes the current minimized state was
    /// its own doing (the off-screen park's iconify on non-macOS).</param>
    /// <param name="reMinimizeLatch">True when the window was USER-minimized before the last
    /// restore-for-interaction summoned it — the park that cleans up afterwards puts it back.</param>
    /// <param name="mode">The resolved park mode (<see cref="BrowserConfiguration.EffectiveParkMode"/>).</param>
    /// <param name="isMacOS">Platform flag (parameterized so macOS transitions are testable here).</param>
    internal static ParkDecision Decide(
        string? windowState, bool iconifiedByUs, bool reMinimizeLatch, ParkMode mode, bool isMacOS)
    {
        if (windowState == "minimized")
        {
            if (!iconifiedByUs)
            {
                // The USER minimized it (we never iconify on macOS, so there any minimize is
                // theirs). It is already better-hidden than we could make it — leave it alone,
                // and keep treating the state as user-owned.
                return new ParkDecision(ParkAction.LeaveMinimized, NormalizeFirst: false, IconifiedByUsAfter: false);
            }

            if (mode != ParkMode.Corner)
            {
                // Our own off-screen park's iconify — already hidden; parking is idempotent.
                return new ParkDecision(ParkAction.LeaveMinimized, NormalizeFirst: false, IconifiedByUsAfter: true);
            }

            // Ours-minimized but the mode is Corner (mode changed mid-profile / stale flag):
            // recover into the corner tile, which requires leaving the minimized state.
            return new ParkDecision(ParkAction.PlaceCorner, NormalizeFirst: true, IconifiedByUsAfter: false);
        }

        if (reMinimizeLatch)
        {
            // The user had it minimized; an interaction summoned it; this is the cleanup park:
            // put it back the way they left it. Not our iconify — a later park must keep
            // treating the minimize as user-owned.
            return new ParkDecision(ParkAction.ReMinimize, NormalizeFirst: false, IconifiedByUsAfter: false);
        }

        var normalizeFirst = windowState is "maximized" or "fullscreen";
        return mode == ParkMode.Corner
            ? new ParkDecision(ParkAction.PlaceCorner, normalizeFirst, IconifiedByUsAfter: false)
            : new ParkDecision(ParkAction.HideOffscreen, normalizeFirst, IconifiedByUsAfter: !isMacOS);
    }

    /// <param name="Action">What to do with the window.</param>
    /// <param name="NormalizeFirst">Set <c>windowState=normal</c> before acting (only ever true
    /// for maximized/fullscreen windows — NEVER for minimized ones; that is the field bug).</param>
    /// <param name="IconifiedByUsAfter">Value for the session's "the current minimized state was
    /// created by us" flag after the action (workspace-wo4q/ynn9 re-dock contract).</param>
    internal readonly record struct ParkDecision(ParkAction Action, bool NormalizeFirst, bool IconifiedByUsAfter);
}
