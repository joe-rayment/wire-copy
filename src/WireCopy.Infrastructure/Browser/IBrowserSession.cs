// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Playwright;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser;

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
    /// Gets a value indicating whether the headed browser window is currently docked
    /// beside the terminal. Drives the persistent "⇉ docked" status-bar affordance so
    /// the "concert" state is always visible (workspace-v7mb). False when headless,
    /// minimized, closed, or no window exists.
    /// </summary>
    bool IsWindowDocked { get; }

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
    /// Gets a value indicating whether the headed browser window is currently
    /// docked to the right half of the screen (concert view). False when
    /// minimized, headless, or no page is active. Used by the dock spotlight
    /// to decide whether selection-follow highlighting should run at all.
    /// </summary>
    bool IsDocked { get; }

    /// <summary>
    /// Gets a value indicating whether a background page can be created in the
    /// dedicated headless preload context. Unlike <see cref="HasBrowserContext"/>
    /// this does NOT require the foreground browser to have been launched first:
    /// the preload context is launched lazily on demand by
    /// <see cref="CreateBackgroundPageAsync"/>, so 'reachable' simply means the
    /// session has not been disposed. Used by default-on background prefetch so
    /// it does not have to wait for the user to navigate once in the foreground.
    /// </summary>
    bool HasPreloadContext { get; }

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
    /// Forcibly disposes the cached Playwright page so the next
    /// <see cref="GetOrCreatePageAsync"/> creates a fresh one. Used to recover
    /// from stale-target errors after the user solves a captcha and the
    /// previous page reference is no longer usable (workspace-m7nc).
    /// The liveness check in <see cref="GetOrCreatePageAsync"/> alone is not
    /// sufficient — it relies on <c>Page.Url</c> throwing, but a half-torn-down
    /// Playwright target can still return a cached URL while every other call
    /// fails.
    /// </summary>
    Task InvalidatePageAsync();

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
    /// Toggles the headed browser window between docked — pinned to the right half
    /// of the screen so the terminal "lens" and the live page sit side by side —
    /// and minimized (full-TUI). Returns the resulting state, or <c>null</c> when
    /// there is no headed window to toggle (headless or no active page).
    /// </summary>
    Task<BrowserWindowState?> ToggleWindowDockAsync();

    /// <summary>
    /// Lens-on-demand: ensures a headed browser window showing <paramref name="url"/>
    /// and docks it to the right half of the screen. Unlike
    /// <see cref="ToggleWindowDockAsync"/> — which only toggles an EXISTING headed
    /// window — this opens (or switches a headless page to) a headed window, navigates
    /// it to the URL, then docks. So pressing the dock key while reading a cached or
    /// headless article summons the live page beside the terminal. The dedicated
    /// headless preload context is left untouched. Returns the resulting window state,
    /// or <c>null</c> when no URL is supplied or the summon fails.
    /// </summary>
    /// <param name="url">URL to open in the headed window (the page the terminal is reading).</param>
    Task<BrowserWindowState?> SummonAndDockAsync(string url);

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
