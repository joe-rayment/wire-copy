// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Visible placement of the headed browser window relative to the terminal UI,
/// returned by <see cref="IBrowserSession.ToggleWindowDockAsync"/>.
/// </summary>
public enum BrowserWindowState
{
    /// <summary>
    /// Hidden out of the way so the terminal UI has the screen. Implemented by PARKING the
    /// window off-screen (workspace-75ng) rather than OS-minimizing it — a parked window keeps
    /// rendering for an instant re-dock — but the user-facing meaning is unchanged: not visible.
    /// </summary>
    Minimized,

    /// <summary>Docked to the right half of the screen, beside the terminal.</summary>
    Docked,
}
