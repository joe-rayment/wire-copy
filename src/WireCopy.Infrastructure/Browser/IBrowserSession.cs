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
    /// the "concert" state is always visible (workspace-v7mb). False when
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
    /// minimized or no page is active. Used by the dock spotlight
    /// to decide whether selection-follow highlighting should run at all.
    /// </summary>
    bool IsDocked { get; }

    /// <summary>
    /// Gets a value indicating whether the user currently wants the sidecar (docked
    /// live window) — the sticky intent behind <see cref="IsDocked"/>. Starts as the
    /// configured <c>Browser:Sidecar</c> default; flipped off when the user toggles
    /// into the immersive view, back on when they dock again. Navigation completion
    /// uses it to auto-engage the sidecar without a manual dock keystroke
    /// (workspace-exbz).
    /// </summary>
    bool WantsSidecar { get; }

    /// <summary>
    /// Gets a value indicating whether the session is attached to the single-window
    /// desktop shell (WIRECOPY_SHELL_CHANNEL connected). Shell mode has its own
    /// pane-reveal semantics (workspace-3keu): the live-page pane reveals once on the
    /// first rendered link list, then stays opt-in via the dock key. Always false in
    /// plain terminal mode.
    /// </summary>
    bool IsShellAttached { get; }

    /// <summary>
    /// Gets or sets the sink for the ONE-TIME user-visible notice raised when a macOS
    /// osascript automation call fails (Automation/Accessibility permission missing) —
    /// wired to the status bar by the orchestrator (workspace-9k27.7). Browse-mode
    /// logs are file-only, so without this the degradation (focus not returning,
    /// terminal not tiling/restoring) is invisible in the TUI. Null = no notice.
    /// </summary>
    Action<string>? PermissionNoticeSink { get; set; }

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
    /// session has crashed. The browser is always launched headful — never
    /// headless (never-headless law; see BrowserSession.LaunchBrowserAsync).
    /// </summary>
    /// <returns>An active Playwright page instance.</returns>
    Task<IPage> GetOrCreatePageAsync();

    /// <summary>
    /// Gets (lazily creating) the dedicated LENS tab — the page the docked sidecar
    /// displays and the ONLY page the dock spotlight navigates (workspace-qigc).
    /// Fetch traffic stays on the page returned by <see cref="GetOrCreatePageAsync"/>,
    /// so follow-navigation and page loads can never interrupt each other. Returns
    /// null when no context exists (not yet launched, no display, disposed) —
    /// callers that need a window summon one first.
    /// </summary>
    Task<IPage?> GetLensPageAsync();

    /// <summary>
    /// Reads the most recent HUMAN input timestamp observed anywhere in the shared
    /// browser (workspace-mya7) — pointer/key/wheel/touch listeners injected at
    /// context launch, plus the e2e test seam. Null when no input has been seen
    /// (or no context exists). Drives the takeover arbiter: the user always wins.
    /// </summary>
    Task<DateTimeOffset?> ReadLastUserInputAsync();

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
    /// No-op if no active page.
    /// </summary>
    Task RestoreWindowAsync();

    /// <summary>
    /// Returns the browser window to its background state so it doesn't cover the
    /// terminal: parked off-screen normally, but RE-DOCKED when the user wants the
    /// sidecar (workspace-exbz) — background quieting must not strip a dock the user
    /// asked for. No-op if no active page.
    /// </summary>
    /// <param name="weJustActivatedBrowser">
    /// workspace-fihe: true ONLY for a post-interaction cleanup where WireCopy just brought the
    /// browser forward for the user (captcha / manual login / Shift+I refresh via
    /// <see cref="RestoreWindowAsync"/>). Such calls hand keyboard focus back to the terminal and
    /// may re-dock the wanted sidecar. Background "get out of the way" callers (prefetch) leave it
    /// false so they never re-activate the terminal or raise the browser on-screen (the focus-war).
    /// </param>
    Task MinimizeWindowAsync(bool weJustActivatedBrowser = false);

    /// <summary>
    /// Toggles the headed browser window between docked — pinned to the right half
    /// of the screen so the terminal "lens" and the live page sit side by side —
    /// and minimized (full-TUI). Returns the resulting state, or <c>null</c> when
    /// there is no headed window to toggle (no active page).
    /// </summary>
    Task<BrowserWindowState?> ToggleWindowDockAsync();

    /// <summary>
    /// Lens-on-demand: ensures a headed browser window showing <paramref name="url"/>
    /// and docks it to the right half of the screen. Unlike
    /// <see cref="ToggleWindowDockAsync"/> — which only toggles an EXISTING headed
    /// window — this opens a headed window if none exists, navigates it to the URL,
    /// then docks. So pressing the dock key while reading a cached article summons
    /// the live page beside the terminal. Returns the resulting window state,
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
    /// workspace-romy.3: captures a screenshot with Set-of-Marks badges —
    /// numbered overlays drawn at each mark's anchor (matched by resolved
    /// href) before the shot and removed after, so the AI analyzer can map
    /// screenshot pixels to link-list indices. Null/empty marks behave like
    /// <see cref="CaptureScreenshotAsync()"/>.
    /// </summary>
    Task<byte[]?> CaptureScreenshotAsync(IReadOnlyList<ScreenshotMark>? marks);

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
