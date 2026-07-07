// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// The imperative steps the dock placement needs, behind a seam (workspace-75ng) so the whole
/// flow in <see cref="SidecarDocker"/> is testable without a real browser or a Mac.
/// <see cref="BrowserSession"/> implements this over CDP; tests implement it as a fake that
/// simulates Retina scaling, a Dock on the right, a menu-bar offset, Chromium clamping the
/// requested width up to a platform minimum, and a phantom work-area read from a just-moved
/// off-screen window.
/// </summary>
internal interface IDockWindowGeometry
{
    /// <summary>Take the window out of any minimized/parked state (windowState = normal).</summary>
    Task NormalizeAsync();

    /// <summary>Iconify the window (windowState = minimized) — used to RETURN a window to the
    /// minimized state the user had put it in before an interaction summoned it (workspace-v7g7).</summary>
    Task IconifyAsync();

    /// <summary>Read the window's current CDP windowState ("normal"/"minimized"/"maximized"/
    /// "fullscreen"), or null when it cannot be read (workspace-v7g7).</summary>
    Task<string?> ReadWindowStateAsync();

    /// <summary>
    /// Put the window into the truly-hidden state (workspace-ynn9): the off-screen move to the
    /// park coordinate, PLUS <c>windowState=minimized</c> on non-macOS — the move hides it under
    /// bare Xvfb (no WM to honor an iconify) while the minimize hides it under a real clamping
    /// window manager (which pulls the off-screen coordinate back on-screen — the stray-tile bug).
    /// workspace-v7g7: this no longer normalizes first; the CALLER must ensure the window is not
    /// minimized/maximized — a minimized window must be LEFT ALONE (the old unconditional
    /// normalize deminiaturized the user's own Cmd+M on every background prefetch park).
    /// </summary>
    Task HideAsync();

    /// <summary>Move the window's top-left to the given point (used to anchor it on-screen).</summary>
    Task MoveAsync(int left, int top);

    /// <summary>Let the window settle after a move before its work area is read (real: a short delay).</summary>
    Task SettleAsync();

    /// <summary>Read the work area of the display the window currently sits on.</summary>
    Task<SidecarGeometry.DisplayInfo?> ReadDisplayAsync();

    /// <summary>Read the window's current outer bounds (to learn the clamped width).</summary>
    Task<TerminalTiling.WindowRect?> ReadWindowAsync();

    /// <summary>Apply the given outer bounds to the window.</summary>
    Task SetWindowAsync(TerminalTiling.WindowRect rect);
}
