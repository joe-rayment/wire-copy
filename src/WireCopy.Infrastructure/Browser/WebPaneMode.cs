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
    /// A live, interactive page (link list, reader-view article, login, captcha, paywall) — stream the
    /// REAL site via the CDP screencast so the user sees and can drive it. The web pane is ALWAYS the
    /// live site when a page is loaded (workspace-8a5y); it never renders a reader-fied snapshot of our
    /// own — a fake website in the pane is useless. Cache-only reads (no live page to stream) collapse
    /// to <see cref="Hidden"/>; the cached content shows in the TUI reader only.
    /// </summary>
    Live,
}
