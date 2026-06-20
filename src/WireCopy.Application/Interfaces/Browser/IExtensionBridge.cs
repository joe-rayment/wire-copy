// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// A snapshot of a page rendered by the user's own browser, captured by the WireCopy Chrome
/// extension's content script and delivered over the <c>/ws/ext</c> control channel (workspace-ozn8).
/// </summary>
/// <param name="Url">The final top-level URL of the captured page (after any redirects/SPA routes).</param>
/// <param name="Html">The post-render <c>document.documentElement.outerHTML</c>.</param>
/// <param name="ViewportWidth">CSS viewport width the page rendered at, in pixels.</param>
/// <param name="ViewportHeight">CSS viewport height the page rendered at, in pixels.</param>
public record ExtensionDomSnapshot(string Url, string Html, int ViewportWidth, int ViewportHeight);

/// <summary>
/// Backend side of the WireCopy extension control channel: lets the orchestrator drive the user's
/// real browser tab (the host-browser-as-renderer architecture, workspace-blg5) instead of a
/// server-side Playwright browser. Active only when the API child runs with
/// <c>WIRECOPY_BROWSER=extension</c> and is connected to a <c>/ws/ext</c> session; otherwise inert.
/// </summary>
public interface IExtensionBridge
{
    /// <summary>True once the Chrome extension has connected and reported <c>ready</c>.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// The underlying tab's current top-level URL, as last reported by the extension
    /// (<c>ready</c> / <c>navigated</c> / <c>domSnapshot</c>). Empty until the first report. Lets the
    /// page-loader capture the already-loaded page instead of re-navigating to it (which would reload
    /// the tab and restart the overlay).
    /// </summary>
    string CurrentUrl { get; }

    /// <summary>Raised when the underlying tab navigates (top-level or SPA route change).</summary>
    event Action<string>? Navigated;

    /// <summary>
    /// Raised when the user interacts directly with the underlying page (click/keypress), so the
    /// orchestrator can treat the host browser as the source of truth.
    /// </summary>
    event Action<string>? UserInteraction;

    /// <summary>
    /// Waits until the extension is connected and ready, or the timeout elapses.
    /// </summary>
    /// <returns>True if ready; false on timeout.</returns>
    Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates the underlying tab to <paramref name="url"/> (top-level) and returns the rendered
    /// DOM once the page settles.
    /// </summary>
    Task<ExtensionDomSnapshot> NavigateAndCaptureAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the current rendered DOM of the underlying tab without navigating (used for SPA
    /// re-extraction and reader view).
    /// </summary>
    Task<ExtensionDomSnapshot> CaptureDomAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scrolls the underlying page to a CSS selector (preferred) or absolute Y offset.
    /// </summary>
    Task<bool> ScrollToAsync(string? selector, double? y, CancellationToken cancellationToken = default);

    /// <summary>
    /// Highlights (spotlights) an element on the underlying page, identified by selector or by the
    /// anchor URL/text the TUI link tree carries.
    /// </summary>
    Task<bool> HighlightAsync(string? selector, string? url, string? text, CancellationToken cancellationToken = default);

    /// <summary>Clears any active spotlight overlay on the underlying page.</summary>
    Task<bool> ClearHighlightAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clicks an element on the underlying page. The target is resolved by the content script in this
    /// priority: viewport coordinates (<paramref name="x"/>/<paramref name="y"/>), then CSS
    /// <paramref name="selector"/>, then the anchor <paramref name="url"/> or visible
    /// <paramref name="text"/> the TUI link tree carries — so the orchestrator can drive a link the
    /// real browser follows natively (its session, JS handlers, in-page fragment jumps) without
    /// reconstructing a selector.
    /// </summary>
    Task<bool> ClickAsync(string? selector, string? url, string? text, double? x, double? y, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles mobile-layout emulation on the underlying tab via CDP device-metrics override
    /// (workspace-8d8a). No-op fallback when the extension declines (e.g. debugger banner refused).
    /// </summary>
    Task<bool> EmulateAsync(bool mobile, int? width, CancellationToken cancellationToken = default);
}
