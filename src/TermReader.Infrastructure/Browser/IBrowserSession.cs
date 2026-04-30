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
    /// Creates a new background page (tab) in the dedicated headless preload
    /// context — a SECOND, completely isolated Playwright persistent context
    /// that lives alongside the foreground browser. The preload context is
    /// always headless, has its own user-data directory, and never shares a
    /// window or tab strip with the user-facing browser. It is launched
    /// lazily on the first call. Cookies are seeded from <c>cookies.json</c>
    /// at launch and can be refreshed mid-session via
    /// <see cref="SyncCookiesToPreloadContextAsync"/>.
    /// Returns null if launching the preload context fails.
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

    /// <summary>
    /// Pushes the supplied cookies into the headless preload context (if it has
    /// been launched) so that background pre-fetches authenticate against
    /// paywalled domains immediately after <c>:cookies import</c> — without
    /// requiring an app restart.
    /// </summary>
    /// <param name="cookies">Cookies captured from the foreground context.</param>
    /// <returns>The number of cookies actually pushed; zero when no preload context exists yet.</returns>
    Task<int> SyncCookiesToPreloadContextAsync(IReadOnlyList<StoredCookie> cookies);
}
