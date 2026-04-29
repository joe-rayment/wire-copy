// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Playwright;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Infrastructure-level interface extending <see cref="IBrowserSessionControl"/>
/// with Playwright-based page access for browser automation components.
/// </summary>
public interface IBrowserSession : IBrowserSessionControl
{
    /// <summary>
    /// Gets a value indicating whether there is an active browser page.
    /// </summary>
    bool HasActiveBrowser { get; }

    /// <summary>
    /// Gets a value indicating whether a browser context exists (even if no page is open).
    /// This is true after the browser is launched, before any page is created.
    /// Used by background preloading to determine if a background tab can be opened.
    /// </summary>
    bool HasBrowserContext { get; }

    /// <summary>
    /// Gets a value indicating whether the browser automation backend is available.
    /// Always true with Playwright (no platform-specific binary issues).
    /// </summary>
    bool IsBrowserAvailable { get; }

    /// <summary>
    /// Gets or creates a Playwright page instance. Returns the existing page
    /// if one is active, or creates a new one if none exists or the previous
    /// session has crashed.
    /// </summary>
    /// <param name="headless">Whether to run the browser in headless mode.</param>
    /// <returns>An active Playwright page instance.</returns>
    Task<IPage> GetOrCreatePageAsync(bool headless);

    /// <summary>
    /// Releases the current page reference without disposing it,
    /// allowing it to be reused by subsequent calls.
    /// </summary>
    void ReleasePage();

    /// <summary>
    /// Restores a minimized browser window to normal size for interactive use.
    /// No-op if headless or no active page.
    /// </summary>
    Task RestoreWindowAsync();

    /// <summary>
    /// Minimizes the browser window so it doesn't cover the terminal.
    /// No-op if headless or no active page.
    /// </summary>
    Task MinimizeWindowAsync();

    /// <summary>
    /// Captures a viewport screenshot of the current page as PNG bytes.
    /// Returns null if no active page or capture fails.
    /// </summary>
    Task<byte[]?> CaptureScreenshotAsync();

    /// <summary>
    /// Creates a new background page (tab) in the existing browser context.
    /// The page shares cookies and session with the main page.
    /// Returns null if no browser context is active.
    /// </summary>
    Task<IPage?> CreateBackgroundPageAsync();

    /// <summary>
    /// Closes a background page previously created via CreateBackgroundPageAsync.
    /// </summary>
    Task CloseBackgroundPageAsync(IPage page);

    /// <summary>
    /// Exports cookies from the current Playwright browser context that match the
    /// given URL. Used by the manual <c>:cookies import</c> path so users who
    /// logged into a paywalled site via the foreground browser can copy session
    /// cookies into the persistent <c>cookies.json</c> store.
    /// </summary>
    /// <param name="url">URL whose cookies should be exported (e.g. <c>https://nytimes.com/</c>).</param>
    /// <returns>The cookies stored in the browser context for that URL, or an empty list when no context is active.</returns>
    Task<IReadOnlyList<StoredCookie>> GetCookiesForUrlAsync(string url);
}
