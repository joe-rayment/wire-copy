// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// How the headed browser window is "hidden" when the user is not looking at it (workspace-v7g7).
/// The window must KEEP RENDERING in every mode (never-headless law + screenshot/AI-curation
/// pipeline), so none of these is an OS minimize by default.
/// </summary>
public enum ParkMode
{
    /// <summary>
    /// Resolve per platform: <see cref="Corner"/> on macOS (which CLAMPS off-screen coordinates
    /// back on-screen — the field-reported "sliver" bug), <see cref="Offscreen"/> everywhere else.
    /// </summary>
    Auto,

    /// <summary>
    /// Move the window to <see cref="BrowserConfiguration.ParkCoordinate"/>; on non-macOS also
    /// iconify so a real clamping window manager still hides it (workspace-ynn9).
    /// </summary>
    Offscreen,

    /// <summary>
    /// Place the window as a deliberate tile flush in the work area's bottom-right corner
    /// (workspace-v7g7). For platforms where true off-screen parking is impossible (macOS clamps
    /// it into a stray sliver that "looks like a mistake"): a corner tile looks intentional,
    /// keeps painting, and never touches focus.
    /// </summary>
    Corner,
}
