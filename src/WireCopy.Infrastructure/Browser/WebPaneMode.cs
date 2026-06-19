// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// What the browser-hosted web pane should show for the current TUI view. The pane is "never empty":
/// when there is nothing meaningful to display it collapses entirely rather than streaming a blank
/// or stale page.
/// </summary>
public enum WebPaneMode
{
    /// <summary>
    /// Nothing to show (launcher, collections, group headers) — collapse the pane and stop the
    /// screencast so it costs nothing.
    /// </summary>
    Hidden,

    /// <summary>
    /// A live, interactive page (link list, login, captcha, paywall) — stream it via the CDP
    /// screencast so the user can see and drive it.
    /// </summary>
    Live,

    /// <summary>
    /// A reader-view article — render our own sanitized HTML snapshot in the pane's iframe for crisp,
    /// selectable text instead of streaming pixels.
    /// </summary>
    Snapshot,
}
