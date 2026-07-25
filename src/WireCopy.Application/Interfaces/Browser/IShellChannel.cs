// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Control channel to the desktop shell (the single-window Electron host). The app keeps
/// all logic and INSTRUCTS the shell's display surface: reveal/hide the live-page pane,
/// switch reader/browser input mode, create hidden tagged pages for the CDP driver to
/// adopt. In plain-terminal mode a null implementation is used and every call no-ops.
/// </summary>
public interface IShellChannel
{
    /// <summary>Raised when the shell switches input mode ("reader" or "browser").</summary>
    event Action<string>? ModeChanged;

    /// <summary>True when a live shell connection exists (running under the desktop shell).</summary>
    bool IsConnected { get; }

    /// <summary>The shell's CDP endpoint (e.g. http://127.0.0.1:9223), or null when not connected.</summary>
    Task<string?> GetCdpEndpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows or hides the live-page pane. Returns false when not connected or on failure.</summary>
    Task<bool> SetPaneVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>Switches shell input mode ("reader" or "browser"). Returns false on failure.</summary>
    Task<bool> SetModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the shell to create a hidden page tagged <paramref name="tag"/> (adoptable over CDP
    /// at about:blank#wc-&lt;tag&gt;). Returns false when not connected or on failure.
    /// </summary>
    Task<bool> CreatePageAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the shell a frame has been RENDERED at the given terminal dimensions
    /// (workspace-tj1z.3): the shell's transition overlay uses this as the deterministic
    /// "resize rewrap done" signal for its settle capture — pty byte heuristics cannot
    /// distinguish the rewrap frame from an unrelated repaint. Returns false when not
    /// connected or on failure.
    /// </summary>
    Task<bool> NotifyResizeRenderedAsync(int cols, int rows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the launcher's view-model to the shell so it can render the launcher as an HTML
    /// pane (workspace-0jxo). <paramref name="visible"/> is true on every launcher render/state
    /// change (carrying <paramref name="state"/>), and false — with a null <paramref name="state"/> —
    /// when a non-launcher screen replaces the launcher. Fire-and-forget from the render path:
    /// callers do not await this, and it never throws (a lost channel fails soft).
    /// </summary>
    Task SendLauncherStateAsync(bool visible, LauncherViewState? state, CancellationToken cancellationToken = default);
    /// <summary>
    /// Pushes the link-tree (story list) view-model to the shell so it can render the
    /// link list as an HTML pane ("Link Tree.dc.html", workspace-vingi).
    /// <paramref name="visible"/> is true on every link-tree render (carrying
    /// <paramref name="state"/>), and false — with a null <paramref name="state"/> — when a
    /// non-link-tree screen replaces it. Fire-and-forget from the render path: callers do
    /// not await this, and it never throws (a lost channel fails soft).
    /// </summary>
    Task SendLinkTreeStateAsync(bool visible, LinkTreeViewState? state, CancellationToken cancellationToken = default);
}
