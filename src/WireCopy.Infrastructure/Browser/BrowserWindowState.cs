// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Visible placement of the headed browser window relative to the terminal UI,
/// returned by <see cref="IBrowserSession.ToggleWindowDockAsync"/>.
/// </summary>
public enum BrowserWindowState
{
    /// <summary>Minimized out of the way — the terminal UI has the screen.</summary>
    Minimized,

    /// <summary>Docked to the right half of the screen, beside the terminal.</summary>
    Docked,
}
