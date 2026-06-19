// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Receives the browser-hosted web pane's desired state from the render path. The orchestrator calls
/// <see cref="Update"/> after every render (the single choke point all selection/navigation changes
/// flow through); the implementation dedupes, forwards a control message to the user's tab, and
/// starts/stops the CDP screencast accordingly. Inert when not running under the web host, so plain
/// terminal runs pay nothing.
/// </summary>
public interface IWebPaneSink
{
    /// <summary>
    /// Sets the pane's desired mode. Cheap and idempotent — safe to call on every render. The
    /// <paramref name="snapshotKey"/> (typically the page URL) dedupes snapshots so identical reader
    /// views are not rebuilt or resent; <paramref name="snapshotHtmlFactory"/> is invoked only when a
    /// new snapshot actually needs to be sent.
    /// </summary>
    void Update(WebPaneMode mode, string? snapshotKey, Func<string>? snapshotHtmlFactory);

    /// <summary>
    /// Toggles the pane's visibility in the user's tab (the 'O' keystroke in web mode). A subsequent
    /// <see cref="Update"/> with a content change re-asserts the content-driven visibility.
    /// </summary>
    void Toggle();
}
