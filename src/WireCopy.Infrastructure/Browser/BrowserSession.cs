// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Shell;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Manages a shared Playwright browser context and page that persists across page loads.
/// Lazily creates the browser on first use and handles session crashes
/// by disposing the broken instance and creating a new one.
/// Uses <c>LaunchPersistentContextAsync</c> to maintain cookies and local storage
/// across sessions via a user-data directory.
/// </summary>
public sealed class BrowserSession : IBrowserSession, IAsyncDisposable
{
    // workspace-75ng: the headed window is PARKED far off-screen when hidden — never in
    // the visible region yet windowState=normal, so Chromium keeps painting it (no
    // minimize/occlusion throttle, helped by --disable-backgrounding-occluded-windows) and
    // the live page is current the instant it re-docks. That flag is a DELIBERATE tradeoff:
    // the parked window keeps rendering (and consuming CPU/battery) even while off-screen,
    // in exchange for an instant-current live page on dock — do not "optimize" it away.
    // workspace-9k27.17: the coordinate itself is BrowserConfiguration.ParkCoordinate —
    // tunable because window managers differ (some Linux WMs clamp/refuse the move,
    // Windows treats -32000 as the legacy minimize coordinate).
    // Shared by the launch path (persistent context) and the desktop-shell attach path.
    private const string AntiDetectionInitScript = @"
                // workspace-mya7: takeover detection — note when a HUMAN uses any
                // page of the shared browser so prefetch can yield instantly.
                // workspace-6yb7.5: input during an armed PickScript session is
                // wizard input, not a takeover — the marker (which owns this
                // signal) skips it rather than PickScript patching the global.
                (() => {
                    const mark = () => {
                        if (window.__wcPick && window.__wcPick.active) return;
                        window.__wcLastUserInput = Date.now();
                    };
                    ['pointerdown', 'keydown', 'wheel', 'touchstart', 'pointermove']
                        .forEach((e) => window.addEventListener(e, mark, { capture: true, passive: true }));
                })();

                // Patch navigator.webdriver (primary automation indicator)
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });

                // Ensure chrome object looks realistic
                if (!window.chrome) window.chrome = {};
                if (!window.chrome.runtime) window.chrome.runtime = {};

                // Patch permissions query to avoid automation detection
                const originalQuery = window.navigator.permissions?.query;
                if (originalQuery) {
                    window.navigator.permissions.query = (parameters) =>
                        parameters.name === 'notifications'
                            ? Promise.resolve({ state: Notification.permission })
                            : originalQuery(parameters);
                }

                // Ensure plugins array is not empty (empty = headless indicator)
                if (navigator.plugins.length === 0) {
                    Object.defineProperty(navigator, 'plugins', {
                        get: () => [1, 2, 3, 4, 5],
                    });
                }

                // Ensure languages array is populated
                if (!navigator.languages || navigator.languages.length === 0) {
                    Object.defineProperty(navigator, 'languages', {
                        get: () => ['en-US', 'en'],
                    });
                }
            ";

    private readonly ILogger<BrowserSession> _logger;
    private readonly ICookieManager _cookieManager;

    // Desktop-shell mode (single-window): live control channel to the Electron shell.
    // NullShellChannel in plain terminal mode, so every _shell.IsConnected branch is dead there.
    private readonly IShellChannel _shell;
    private readonly string _userDataDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;

    // Shell attach mode: the CDP connection to the shell's embedded Chromium, and a counter
    // for unique page tags (fetch-N / bg-N) so re-attaches never adopt a stale page.
    private IBrowser? _attachedBrowser;
    private int _shellPageCounter;

    // workspace-qigc: dedicated LENS tab — the page the docked sidecar shows and the
    // dock spotlight navigates. Fetches (PageLoader) keep _page to themselves, so a
    // follow-navigation can never interrupt a load and a load can never blank the
    // page the user is reading beside the terminal.
    private IPage? _lensPage;
    private bool _isDocked;

    // Sticky user intent (workspace-v7mb): distinct from _isDocked (the window's current
    // visible state). Set when the user docks, cleared when they explicitly un-dock via the
    // toggle. Survives the auto-park-off-screen at launch (workspace-75ng) so a headed window
    // that reappears (e.g. after a crash recovery) re-docks itself when the user wanted a dock.
    private bool _userWantsDock;

    private bool _disposed;
    private bool _browsersInstalled;

    // workspace-fihe follow-up: 1 when RestoreWindowAsync brought the browser forward for a user
    // interaction and no cleanup has handed keyboard focus back yet. Consumed by the next
    // MinimizeWindowAsync (many restore flows have no dedicated cleanup and rely on the next
    // background hide) and cleared whenever a weJustActivatedBrowser refocus actually runs.
    private int _pendingInteractionHandback;

    public BrowserSession(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<BrowserSession> logger,
        ICookieManager cookieManager,
        IShellChannel? shellChannel = null)
    {
        _logger = logger;
        _cookieManager = cookieManager;
        _shell = shellChannel ?? new NullShellChannel();

        // Sidecar mode starts in the configured state (workspace-exbz): when on, the
        // shell reveals the pane on first attach; the dock toggle drops it by clearing
        // this intent. Default OFF (workspace-75ng).
        _userWantsDock = browserConfig.Value.Sidecar;
        _userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "browser-profile");
    }

    /// <inheritdoc />
    // Reference-typed field reads are atomic in .NET; we don't need to take _lock
    // just to ask "is this currently non-null?" The reader may observe the prior
    // state if a launch/disposal is mid-flight, which is acceptable for this probe.
    public bool HasActiveBrowser => !_disposed && _page != null;

    /// <inheritdoc />
    public bool IsWindowDocked => !_disposed && _isDocked;

    /// <inheritdoc />
    public bool HasBrowserContext => !_disposed && _context != null;

    /// <inheritdoc />
    // Background tabs are created lazily in the SHARED context by
    // CreateBackgroundPageAsync (workspace-wo4q); 'reachable' = not disposed.
    public bool HasPreloadContext => !_disposed;

    /// <inheritdoc />
    public bool IsBrowserAvailable => true;

    /// <inheritdoc />
    // Same atomic-read rationale as HasActiveBrowser: a momentarily stale value
    // is acceptable for this probe — the spotlight re-checks on every sync.
    public bool IsDocked => !_disposed && _isDocked && _page != null;

    /// <inheritdoc />
    public bool WantsSidecar => !_disposed && _userWantsDock;

    /// <inheritdoc />
    public bool IsShellAttached => !_disposed && _shell.IsConnected;

    /// <inheritdoc />
    public async Task<DateTimeOffset?> ReadLastUserInputAsync()
    {
        if (_disposed)
        {
            return null;
        }

        // Test seam (workspace-mya7): e2e harnesses cannot inject real input into
        // the app-owned browser, so an mtime-touched file stands in for it.
        var seam = Environment.GetEnvironmentVariable("WIRECOPY_TEST_USERINPUT_FILE");
        DateTimeOffset? latest = null;
        if (!string.IsNullOrEmpty(seam) && File.Exists(seam))
        {
            latest = File.GetLastWriteTimeUtc(seam);
        }

        var ctx = _context;
        if (ctx == null)
        {
            return latest;
        }

        foreach (var page in ctx.Pages)
        {
            try
            {
                var ms = await page.EvaluateAsync<double>("() => window.__wcLastUserInput || 0").ConfigureAwait(false);
                if (ms > 0)
                {
                    var ts = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
                    if (latest == null || ts > latest)
                    {
                        latest = ts;
                    }
                }
            }
            catch (Exception)
            {
                // Mid-navigation/closed page — skip; the next poll re-reads.
            }
        }

        return latest;
    }

    /// <inheritdoc />
    public async Task<IPage?> GetLensPageAsync()
    {
        if (_disposed)
        {
            return null;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _context == null)
            {
                // The lens only exists beside a launched (always headed) window; callers
                // that need one summon it first (SummonAndDockAsync → GetOrCreatePageAsync).
                return null;
            }

            if (_lensPage != null)
            {
                try
                {
                    _ = _lensPage.Url;
                    return _lensPage;
                }
                catch (PlaywrightException)
                {
                    _lensPage = null; // dead tab — recreate below
                }
            }

            if (_shell.IsConnected)
            {
                // Single-window shell: the lens IS the shell's visible pane — adopt it over
                // CDP. NewPageAsync would create a detached target no pane ever shows.
                await _shell.CreatePageAsync("lens").ConfigureAwait(false);
                _lensPage = await AdoptTaggedPageAsync("lens").ConfigureAwait(false);
                if (_lensPage != null)
                {
                    _lensPage.Close += OnLensPageClosed;
                    _logger.LogDebug("Adopted the shell's lens pane");
                }

                return _lensPage;
            }

            // Unattended host (NullShellChannel): there is no pane to show a lens in.
            _logger.LogDebug("No shell attached — lens unavailable");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create lens tab (non-fatal)");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IPage> GetOrCreatePageAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_page != null)
            {
                // Verify the existing page is still alive
                try
                {
                    _ = _page.Url;
                    _logger.LogDebug("Reusing existing Playwright page");
                    return _page;
                }
                catch (PlaywrightException ex)
                {
                    _logger.LogWarning(ex, "Existing Playwright page is dead, creating a new one");
                    await DisposeContextUnsafeAsync().ConfigureAwait(false);
                }
            }

            // NEVER-HEADLESS POLICY: the browser is always launched headful — LaunchBrowserAsync
            // hardcodes Headless=false. Headless is bot-detected/blocked on this app's target
            // sites, so it is disabled unconditionally and there is no headless code path at all.
            _logger.LogInformation("Creating new Playwright browser session (headful — never headless)");
            try
            {
                await LaunchBrowserAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException ex) when (LooksLikeMissingDisplay(ex.Message))
            {
                // No X server / DISPLAY. We do NOT fall back to headless (workspace-8ne3 / never-headless
                // policy — headless gets bot-blocked, which is the whole reason it's banned). Fail loudly with
                // an actionable message: run under a virtual display. The `run` script auto-provides one
                // (xvfb-run) on Linux; a bare/cron invocation must wrap itself: `xvfb-run -a <cmd>`.
                _logger.LogError(ex,
                    "Headed browser launch failed — no display (DISPLAY/WAYLAND_DISPLAY unset). WireCopy never "
                    + "runs headless. Start it under a virtual display, e.g. `xvfb-run -a ./run`, or attach a real display.");
                throw new InvalidOperationException(
                    "Headed browser launch failed: no display available, and headless is disabled by policy. "
                    + "Run under a virtual display (e.g. `xvfb-run -a ./run`) or attach a real display.",
                    ex);
            }

            return _page!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WarmUpAsync()
    {
        _logger.LogDebug("Warming up browser session (headful — never headless)");
        await GetOrCreatePageAsync().ConfigureAwait(false);
        _logger.LogDebug("Browser session warm-up complete");
    }

    /// <inheritdoc />
    public void ReleasePage()
    {
        // No-op: the session retains the page for reuse.
        // The page is only disposed when the session itself is disposed
        // or when a crash is detected.
    }

    /// <inheritdoc />
    public async Task InvalidatePageAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_page == null)
            {
                return;
            }

            _logger.LogInformation("InvalidatePageAsync: forcing fresh Playwright page on next request");
            await DisposeContextUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RestoreWindowAsync()
    {
        // Unattended host (NullShellChannel): no user is present and the off-screen browser
        // window is not managed — nothing to bring forward.
        if (_disposed || _page == null || !_shell.IsConnected)
        {
            return;
        }

        // Single-window shell: "bring the browser forward for interaction" (captcha,
        // manual login) = reveal the pane and hand it the keyboard (browser mode).
        // Esc returns to the reader; the next MinimizeWindowAsync hand-back also
        // switches the mode back. No OS window exists to un-minimize or raise.
        Interlocked.Exchange(ref _pendingInteractionHandback, 1);
        if (await _shell.SetPaneVisibleAsync(true).ConfigureAwait(false))
        {
            _isDocked = true;
        }

        await _shell.SetModeAsync("browser").ConfigureAwait(false);
    }

    /// <inheritdoc />
    // workspace-75ng: "get out of the way" now PARKS the window off-screen rather than
    // OS-minimizing it. A minimized window is occlusion-throttled by Chromium — its live
    // page stops painting, so the spotlight/screenshots go stale. Parking keeps it at
    // windowState=normal off-screen: invisible and focus-neutral, but still rendering.
    public async Task MinimizeWindowAsync(bool weJustActivatedBrowser = false)
    {
        if (_disposed || _page == null)
        {
            return;
        }

        if (_shell.IsConnected)
        {
            // Single-window shell: nothing to park — reconcile pane visibility with the
            // sticky dock intent, and hand the keyboard back to the reader when this is an
            // interaction-cleanup call. Background prefetch calls stay pane-neutral.
            var shellHandBack = weJustActivatedBrowser
                || Interlocked.Exchange(ref _pendingInteractionHandback, 0) == 1;
            if (_userWantsDock && !_isDocked)
            {
                _isDocked = await _shell.SetPaneVisibleAsync(true).ConfigureAwait(false);
            }
            else if (!_userWantsDock && _isDocked)
            {
                await _shell.SetPaneVisibleAsync(false).ConfigureAwait(false);
                _isDocked = false;
            }

            if (shellHandBack)
            {
                await _shell.SetModeAsync("reader").ConfigureAwait(false);
            }

            return;
        }

        // Unattended host (NullShellChannel): the browser window lives off-screen for the whole
        // run — nothing to park or hand focus back to. Consume the latch so it can't go stale.
        Interlocked.Exchange(ref _pendingInteractionHandback, 0);
    }

    /// <inheritdoc />
    public async Task<BrowserWindowState?> ToggleWindowDockAsync()
    {
        if (_disposed || _page == null)
        {
            return null;
        }

        if (_shell.IsConnected)
        {
            if (_isDocked)
            {
                // Explicit un-dock: drop the sticky intent, hide the pane, reader keeps keys.
                _userWantsDock = false;
                await _shell.SetPaneVisibleAsync(false).ConfigureAwait(false);
                _isDocked = false;
                await _shell.SetModeAsync("reader").ConfigureAwait(false);
                return BrowserWindowState.Minimized;
            }

            return await DockWindowAsync().ConfigureAwait(false);
        }

        // Unattended host (NullShellChannel): there is no pane to dock.
        return null;
    }

    /// <inheritdoc />
    public async Task<PaneOpenResult> OpenInPaneAsync(string url)
    {
        if (_disposed || !_shell.IsConnected || string.IsNullOrWhiteSpace(url))
        {
            return PaneOpenResult.NotAttached;
        }

        try
        {
            // Reveal the pane FIRST (instant feedback; sticky dock intent — this is the
            // user's explicit "show me this in the browser" gesture), then adopt the lens
            // and drive it. Lens adoption can queue behind a prefetch holding the session
            // lock — the log brackets make that wait visible in the field.
            _logger.LogInformation("Opening in pane: {Url}", url);
            await GetOrCreatePageAsync().ConfigureAwait(false);
            if (await DockWindowAsync().ConfigureAwait(false) != BrowserWindowState.Docked)
            {
                throw new InvalidOperationException("The shell pane did not reveal");
            }

            var lens = await GetLensPageAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The shell lens page is unavailable");
            await lens.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000,
            }).ConfigureAwait(false);
            await _shell.SetModeAsync("browser").ConfigureAwait(false);

            // A queued background MinimizeWindowAsync must not consume a STALE
            // interaction latch and stomp the mode back to reader moments after the
            // user's explicit open — their gesture supersedes any pending hand-back.
            Interlocked.Exchange(ref _pendingInteractionHandback, 0);
            _logger.LogInformation("Opened in pane: {Url}", url);
            return PaneOpenResult.Opened;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open-in-pane failed for {Url}", url);
            return PaneOpenResult.Failed;
        }
    }

    /// <inheritdoc />
    public async Task<BrowserWindowState?> SummonAndDockAsync(string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            // Ensure the (always headed) window exists.
            await GetOrCreatePageAsync().ConfigureAwait(false);

            // Make sure the lens tab exists — docking activates it. The summon does NOT
            // navigate anymore (workspace-u4o9): navigation is the dock spotlight's job
            // (it follow-navigates the lens right after the post-dock render), and that
            // single ownership is what guarantees a summon can never interrupt a fetch
            // in flight on the foreground page.
            await GetLensPageAsync().ConfigureAwait(false);

            // A freshly (re)launched headed window starts minimized (LaunchBrowserAsync),
            // so force-dock rather than toggle.
            return await DockWindowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // workspace-2hfr: Warning, not Debug — this is the exception behind the
            // user-facing "Couldn't open the live page beside the app" line, and the
            // file log is the only place the actual cause can be diagnosed from.
            _logger.LogWarning(ex, "SummonAndDockAsync failed for {Url}", url);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<byte[]?> CaptureScreenshotAsync() => CaptureScreenshotAsync(marks: null);

    /// <inheritdoc />
    public async Task<byte[]?> CaptureScreenshotAsync(IReadOnlyList<ScreenshotMark>? marks)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _page == null)
            {
                return null;
            }

            // workspace-romy.3: draw Set-of-Marks badges before the shot so the
            // AI analyzer can map pixels to link indices. Best-effort — a failed
            // apply degrades to an unmarked screenshot.
            var marked = false;
            if (marks is { Count: > 0 })
            {
                try
                {
                    var drawn = await _page.EvaluateAsync<int>(
                        ScreenshotMarkScript.Apply,
                        marks.Select(m => new { i = m.Index, url = m.Url }).ToArray()).ConfigureAwait(false);
                    marked = true;

                    // Information level on purpose: badge alignment is the
                    // grounding contract — live gates assert on this line.
                    _logger.LogInformation("Set-of-Marks: drew {Drawn} of {Requested} badges", drawn, marks.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "Set-of-Marks apply failed, capturing unmarked (non-fatal)");
                }
            }

            try
            {
                // workspace-romy.1: capture past the fold — a viewport-only shot
                // of a news homepage shows the masthead and little else, so the
                // AI layout analyzer could never see the river it was ranking.
                // Playwright's ScreenshotAsync cannot clip beyond the viewport,
                // so go through CDP Page.captureScreenshot with
                // captureBeyondViewport: up to three viewport heights tall.
                // Quirks-mode pages report the real height on body.scrollHeight
                // while documentElement.scrollHeight is just the viewport —
                // take the max of both.
                var dims = await _page.EvaluateAsync<int[]>(
                    @"() => {
                        const de = document.documentElement;
                        const vw = window.innerWidth || de.clientWidth || 1280;
                        const vh = window.innerHeight || de.clientHeight || 800;
                        const sh = Math.max(de.scrollHeight || 0, document.body ? document.body.scrollHeight : 0, vh);
                        return [vw, Math.min(sh, vh * 3)];
                    }").ConfigureAwait(false);
                if (dims is { Length: 2 } && dims[0] > 0 && dims[1] > 0)
                {
                    var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
                    var shot = await cdp.SendAsync("Page.captureScreenshot", new Dictionary<string, object>
                    {
                        ["format"] = "png",
                        ["captureBeyondViewport"] = true,
                        ["clip"] = new Dictionary<string, object>
                        {
                            ["x"] = 0,
                            ["y"] = 0,
                            ["width"] = dims[0],
                            ["height"] = dims[1],
                            ["scale"] = 1,
                        },
                    }).ConfigureAwait(false);
                    var data = shot?.GetProperty("data").GetString();
                    if (!string.IsNullOrEmpty(data))
                    {
                        return Convert.FromBase64String(data);
                    }
                }

                return await _page.ScreenshotAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Beyond-viewport screenshot failed, retrying viewport-only (non-fatal)");
                try
                {
                    return await _page.ScreenshotAsync().ConfigureAwait(false);
                }
                catch (Exception ex2)
                {
                    _logger.LogDebug(ex2, "Failed to capture screenshot (non-fatal)");
                    return null;
                }
            }
            finally
            {
                if (marked)
                {
                    try
                    {
                        await _page.EvaluateAsync<bool>(ScreenshotMarkScript.Remove).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Set-of-Marks removal failed (non-fatal)");
                    }
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IPage?> CreateBackgroundPageAsync()
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            // workspace-wo4q: background pages are TABS of the user's ONE real
            // browser — one profile, one cookie jar, prefetch visible to anyone who
            // brings the window up. Ensure the main context exists at the resolved
            // visibility; never launch a second context.
            if (_context == null)
            {
                await GetOrCreatePageAsync().ConfigureAwait(false);
            }

            var ctx = _context;
            if (ctx == null)
            {
                return null;
            }

            if (_shell.IsConnected)
            {
                // Single-window shell: hidden pages are created BY the shell (invisible views
                // of the one headful window — quiet by construction, nothing can pop or steal
                // focus) and adopted here over CDP.
                var tag = $"bg-{Interlocked.Increment(ref _shellPageCounter)}";
                if (!await _shell.CreatePageAsync(tag).ConfigureAwait(false))
                {
                    return null;
                }

                var shellPage = await AdoptTaggedPageAsync(tag).ConfigureAwait(false);
                if (shellPage == null)
                {
                    return null;
                }

                await ApplyFetchViewportAsync(shellPage).ConfigureAwait(false);
                _logger.LogDebug("Created hidden shell page {Tag} (total pages: {Count})", tag, ctx.Pages.Count);
                return shellPage;
            }

            // workspace-v7g7: create the tab QUIETLY — CDP Target.createTarget background:true is
            // a ctrl-click "open in background tab": the active tab never changes and nothing is
            // raised, so no compensation is needed. The old NewPageAsync activated the new tab
            // and the compensating Page.bringToFront OS-activated Chromium on macOS — every
            // prefetch-tab (re)creation popped the browser over the user's work.
            var page = await CreateBackgroundPageQuietlyAsync(ctx).ConfigureAwait(false);

            if (page == null)
            {
                // Fallback (CDP path unavailable): the classic creation. Tab etiquette: creating
                // a tab activates it in a headed window — hand the window straight back to the
                // lens/fetch tab so prefetch never steals the page the user is reading
                // (workspace-wo4q). Information on purpose — the e2e gate asserts this
                // activating path never runs.
                page = await ctx.NewPageAsync().ConfigureAwait(false);
                _logger.LogInformation("Background tab created via fallback NewPageAsync (activating)");
                var front = _lensPage ?? _page;
                if (front != null)
                {
                    try
                    {
                        await front.BringToFrontAsync().ConfigureAwait(false);
                    }
                    catch (PlaywrightException ex)
                    {
                        _logger.LogDebug(ex, "Could not re-activate the front tab after creating a background tab");
                    }
                }
            }

            // workspace-v7g7: the context is viewport-less — give the prefetch tab the same
            // fixed viewport as the fetch page so prefetched pages render/extract identically.
            await ApplyFetchViewportAsync(page).ConfigureAwait(false);

            _logger.LogDebug(
                "Created background tab in the shared context (total pages: {Count})",
                ctx.Pages.Count);
            return page;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create background tab");
            return null;
        }
    }

    /// <inheritdoc />
    public Task<int> SyncCookiesToPreloadContextAsync(IReadOnlyList<StoredCookie> cookies)
    {
        // workspace-wo4q: single-context mode — background prefetch shares the ONE
        // browser profile, so cookies imported into the foreground context are
        // already visible to prefetch. Nothing to sync; kept for interface
        // compatibility with the cookie-import flow.
        _logger.LogDebug("Cookie sync skipped — prefetch shares the foreground browser context");
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredCookie>> GetCookiesForUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Array.Empty<StoredCookie>();
        }

        if (_disposed)
        {
            return Array.Empty<StoredCookie>();
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _context == null)
            {
                _logger.LogDebug("No active browser context — cannot export cookies for {Url}", url);
                return Array.Empty<StoredCookie>();
            }

            try
            {
                var playwrightCookies = await _context.CookiesAsync(new[] { url }).ConfigureAwait(false);
                if (playwrightCookies.Count == 0)
                {
                    return Array.Empty<StoredCookie>();
                }

                return playwrightCookies
                    .Select(c => new StoredCookie(
                        c.Name,
                        c.Value,
                        c.Domain ?? string.Empty,
                        c.Path ?? string.Empty,
                        c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).UtcDateTime : null))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to export cookies for {Url} (non-fatal)", url);
                return Array.Empty<StoredCookie>();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task CloseBackgroundPageAsync(IPage page)
    {
        try
        {
            await page.CloseAsync().ConfigureAwait(false);
            _logger.LogDebug("Closed background page");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing background page (non-fatal)");
        }
    }

    /// <inheritdoc />
    // Sync disposal kept for IDisposable consumers; the DI container will prefer
    // DisposeAsync below when both are present, avoiding sync-over-async.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        bool lockAcquired;
        try
        {
            lockAcquired = await _lock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!lockAcquired)
        {
            _logger.LogWarning("BrowserSession.Dispose timed out waiting for lock — proceeding with best-effort cleanup");
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await DisposeContextUnsafeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during BrowserSession disposal");
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    _lock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed
                }
            }

            _lock.Dispose();
        }
    }

    // NEVER-HEADLESS POLICY (workspace-8ne3): no `headless` parameter — Chromium is ALWAYS launched headful.
    // Headless is bot-detected/blocked on this app's target sites (Cloudflare, NYT, macleans.ca), so it is
    // disabled unconditionally. On a display-less host the headful browser runs under a virtual display (the
    // `run` script provides Xvfb); it never silently degrades to headless.
    private async Task LaunchBrowserAsync()
    {
        if (_shell.IsConnected)
        {
            // Single-window shell: never launch a second browser — attach to the shell's
            // embedded Chromium over CDP. Headful by construction (the shell IS the window),
            // so none of the terminal-focus/park/window plumbing below applies.
            await AttachToShellAsync().ConfigureAwait(false);
            return;
        }

        // Direct launch — UNATTENDED verbs only (run-recipe / resolve-section / scheduled
        // runs; the interactive app always attaches to the shell). Headful under a virtual
        // display, parked off-screen for the whole run via the launch arg below — no OS
        // window management, focus hand-back, or terminal tiling exists on this path.
        // Clean up any leftover state from a previous failed launch
        await DisposeContextUnsafeAsync().ConfigureAwait(false);

        Directory.CreateDirectory(_userDataDir);

        // workspace-wo4q migration: the separate always-headless preload profile is
        // retired (prefetch shares the ONE browser profile). Sweep the orphan dir so
        // stale cookies/cache don't linger on disk. Best-effort, once per launch.
        try
        {
            var orphanProfile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WireCopy",
                "preload-profile");
            if (Directory.Exists(orphanProfile))
            {
                Directory.Delete(orphanProfile, recursive: true);
                _logger.LogInformation("Removed the retired preload-profile directory (single-context mode)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove the retired preload-profile directory (non-fatal)");
        }

        // Ensure Playwright browsers are installed (idempotent — skips if already present).
        // Redirect stdout/stderr so install progress doesn't corrupt the TUI alternate screen.
        // Run with a timeout to prevent indefinite hangs on network issues.
        if (!_browsersInstalled)
        {
            _browsersInstalled = true;
            try
            {
                _logger.LogInformation("Ensuring Playwright browsers are installed...");
                var installTask = Task.Run(() =>
                {
                    var origOut = Console.Out;
                    var origErr = Console.Error;
                    Console.SetOut(TextWriter.Null);
                    Console.SetError(TextWriter.Null);
                    try
                    {
                        return Microsoft.Playwright.Program.Main(["install", "chromium"]);
                    }
                    finally
                    {
                        Console.SetOut(origOut);
                        Console.SetError(origErr);
                    }
                });

                if (await Task.WhenAny(installTask, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) == installTask)
                {
                    var exitCode = await installTask.ConfigureAwait(false);
                    if (exitCode != 0)
                    {
                        _logger.LogWarning("Playwright browser install returned exit code {ExitCode}", exitCode);
                    }
                }
                else
                {
                    _logger.LogWarning("Playwright browser install timed out after 60s (will attempt launch anyway)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playwright browser install failed (will attempt launch anyway)");
            }
        }

        try
        {
            var createTask = Playwright.CreateAsync();
            if (await Task.WhenAny(createTask, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != createTask)
            {
                throw new TimeoutException("Playwright.CreateAsync timed out after 15 seconds");
            }

            _playwright = await createTask.ConfigureAwait(false);

            var args = new List<string>
            {
                "--disable-blink-features=AutomationControlled",
                "--disable-infobars",

                // workspace-75ng: keep the off-screen/occluded window PAINTING so the live
                // page is current the instant the user docks it — otherwise Chromium throttles
                // hidden windows and the spotlight/screenshots go stale.
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
            };

            // Unattended run: spawn the headed window at a fixed off-screen position so it
            // stays out of the way for the whole run (bare Xvfb honors the coordinate; a
            // real WM clamping it on-screen is acceptable when nobody is watching).
            args.Add("--window-position=20000,20000");

            if (OperatingSystem.IsLinux())
            {
                args.AddRange(["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]);
            }

            args.Add("--window-size=800,600");

            var launchTask = _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false, // never-headless policy — always headful
                Args = args.ToArray(),
                Timeout = 30000, // 30s timeout prevents indefinite hang if browser can't start

                // Headed mode: let Chromium use its real UA to avoid version-mismatch detection (e.g. claiming
                // Chrome/131 but actually running Chromium/145).
                UserAgent = null,

                // workspace-v7g7: NoViewport, NOT null. In Playwright/.NET `null` means the DEFAULT
                // 1280x720 EMULATED viewport — Patchright then sizes the OS window to fit it
                // (1288x851) overriding our --window-size, silently re-asserts that size after
                // navigations (reverting every park/corner placement), and overrides the JS screen
                // metrics with the emulated 1280x720 (the workspace-r2we "0.8x work area" dock
                // skew on a 1600px display). With NoViewport the OS window is entirely ours; the
                // fetch/prefetch pages get the exact same 1280x720 viewport re-applied PER PAGE
                // below, so rendering/extraction behavior is unchanged.
                ViewportSize = ViewportSize.NoViewport,
                IgnoreHTTPSErrors = true,
            });

            // Additional timeout guard — LaunchPersistentContextAsync's own Timeout may not
            // cover all hang scenarios (e.g., no display for headed mode, broken Xvfb)
            if (await Task.WhenAny(launchTask, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != launchTask)
            {
                throw new TimeoutException("Browser launch timed out after 30 seconds");
            }

            _context = await launchTask.ConfigureAwait(false);

            await _context.AddInitScriptAsync(AntiDetectionInitScript).ConfigureAwait(false);

            _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync().ConfigureAwait(false);

            // workspace-v7g7: the context has NO Playwright-managed viewport (see the launch
            // options) — re-apply the exact viewport the app has always rendered and extracted
            // at (Playwright's old context default) to the fetch page itself.
            await ApplyFetchViewportAsync(_page).ConfigureAwait(false);

            // Inject stored cookies on every launch so mid-session updates
            // (e.g., Shift+I login) are picked up on browser restart.
            await InjectStoredCookiesAsync().ConfigureAwait(false);

            // The browser is always headful (never-headless policy), so the headed-window wiring always runs.
            // Reset the docked flag if THIS headed window is later closed/crashes so the persistent "docked"
            // affordance can't lie (workspace-v7mb).
            _page!.Close += OnHeadedPageClosed;
        }
        catch
        {
            // Clean up partial state so the next call starts fresh
            await DisposeContextUnsafeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Desktop-shell attach path: connects Patchright to the shell's embedded Chromium
    /// (ConnectOverCDPAsync) and adopts a fresh hidden shell page as the fetch tab.
    /// Replaces LaunchPersistentContextAsync entirely — one browser, one window, no
    /// OS-window management, headful by construction.
    /// </summary>
    private async Task AttachToShellAsync()
    {
        await DisposeContextUnsafeAsync().ConfigureAwait(false);

        var endpoint = await _shell.GetCdpEndpointAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Shell channel is connected but provided no CDP endpoint");

        var createTask = Playwright.CreateAsync();
        if (await Task.WhenAny(createTask, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != createTask)
        {
            throw new TimeoutException("Playwright.CreateAsync timed out after 15 seconds");
        }

        _playwright = await createTask.ConfigureAwait(false);

        var connectTask = _playwright.Chromium.ConnectOverCDPAsync(endpoint);
        if (await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != connectTask)
        {
            throw new TimeoutException($"ConnectOverCDPAsync({endpoint}) timed out after 15 seconds");
        }

        _attachedBrowser = await connectTask.ConfigureAwait(false);

        // Fresh hidden fetch tab, uniquely tagged so a re-attach never adopts a stale page.
        var tag = $"fetch-{Interlocked.Increment(ref _shellPageCounter)}";
        if (!await _shell.CreatePageAsync(tag).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The shell failed to create the fetch page");
        }

        _page = await AdoptTaggedPageAsync(tag).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Could not adopt the shell page tagged '{tag}' over CDP");
        _context = _page.Context;

        try
        {
            await _context.AddInitScriptAsync(AntiDetectionInitScript).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            // Defense-in-depth only: the shell pages already ARE a real headful browser.
            _logger.LogDebug(ex, "Init script not applied on the attached context (non-fatal)");
        }

        await ApplyFetchViewportAsync(_page).ConfigureAwait(false);
        await InjectStoredCookiesAsync().ConfigureAwait(false);
        _page.Close += OnHeadedPageClosed;
        _logger.LogInformation(
            "Attached to the desktop shell browser at {Endpoint} (single-window, headful)", endpoint);

        if (_userWantsDock)
        {
            _isDocked = await _shell.SetPaneVisibleAsync(true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the shell page whose URL carries the #wc-&lt;tag&gt; marker (the shell tags every
    /// page it creates). Polls briefly because CDP target attachment races page creation.
    /// </summary>
    private async Task<IPage?> AdoptTaggedPageAsync(string tag)
    {
        var marker = "#wc-" + tag;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var browser = _attachedBrowser;
            if (browser == null)
            {
                return null;
            }

            foreach (var ctx in browser.Contexts)
            {
                foreach (var page in ctx.Pages)
                {
                    try
                    {
                        if (page.Url.EndsWith(marker, StringComparison.Ordinal))
                        {
                            return page;
                        }
                    }
                    catch (PlaywrightException)
                    {
                        // Page torn down mid-scan — skip it.
                    }
                }
            }

            await Task.Delay(150).ConfigureAwait(false);
        }

        return null;
    }

#pragma warning disable SA1202 // helper kept adjacent to its sole caller (LaunchBrowserAsync) for readability (workspace-j0b8).
    /// <summary>
    /// Heuristic: did the Chromium launch fail because there is no X server / DISPLAY?
    /// The Chromium process logs ozone_platform_x11 / aura.env errors which Playwright
    /// surfaces inside the TargetClosedException's <c>Browser logs</c> panel. We match on
    /// the Playwright-team-formatted banner so we don't false-positive on Chrome crashes
    /// for unrelated reasons. (workspace-j0b8)
    /// </summary>
    internal static bool LooksLikeMissingDisplay(string errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("Missing X server", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("$DISPLAY", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("launched a headed browser without having a XServer", StringComparison.OrdinalIgnoreCase);
    }
#pragma warning restore SA1202

    private async Task InjectStoredCookiesAsync()
    {
        await InjectStoredCookiesIntoAsync(_context, "foreground").ConfigureAwait(false);
    }

    private async Task InjectStoredCookiesIntoAsync(IBrowserContext? context, string label)
    {
        if (context == null)
        {
            return;
        }

        try
        {
            var cookies = await _cookieManager.LoadCookiesAsync().ConfigureAwait(false);
            if (cookies.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pwCookies = cookies
                .Select(c => new Cookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expires = c.Expiry.HasValue
                        ? new DateTimeOffset(c.Expiry.Value).ToUnixTimeSeconds()
                        : -1,
                })
                .Where(c => c.Expires < 0 || c.Expires > now)
                .ToList();

            await context.AddCookiesAsync(pwCookies).ConfigureAwait(false);
            _logger.LogDebug(
                "Injected {Count} stored cookies into Playwright {Label} context",
                pwCookies.Count,
                label);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to inject stored cookies into Playwright {Label} context (non-fatal)",
                label);
        }
    }

    /// <summary>
    /// Creates a tab in the shared context WITHOUT activating it (workspace-v7g7):
    /// <c>Target.createTarget background:true</c> over CDP, then adopts the Playwright page that
    /// attaches for it. Returns null when anything about the CDP path fails — the caller
    /// (<see cref="CreateBackgroundPageAsync"/>) falls back to the classic (activating) creation.
    /// </summary>
    private async Task<IPage?> CreateBackgroundPageQuietlyAsync(IBrowserContext ctx)
    {
        if (_page == null)
        {
            return null;
        }

        try
        {
            var existing = ctx.Pages.ToList();
            var tcs = new TaskCompletionSource<IPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPage(object? sender, IPage p)
            {
                if (!existing.Contains(p))
                {
                    tcs.TrySetResult(p);
                }
            }

            ctx.Page += OnPage;
            try
            {
                var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
                var created = await cdp.SendAsync("Target.createTarget", new Dictionary<string, object>
                {
                    ["url"] = "about:blank",
                    ["background"] = true,
                }).ConfigureAwait(false);
                if (created?.TryGetProperty("targetId", out _) != true)
                {
                    _logger.LogDebug("Quiet background-tab creation returned no targetId; falling back");
                    return null;
                }

                // The page may have attached before or after the createTarget response — check
                // the snapshot diff first, then wait on the event.
                var adopted = ctx.Pages.FirstOrDefault(p => !existing.Contains(p));
                if (adopted == null)
                {
                    var winner = await Task.WhenAny(tcs.Task, Task.Delay(5000)).ConfigureAwait(false);
                    if (winner != tcs.Task)
                    {
                        _logger.LogDebug("Quiet background-tab creation timed out waiting for the page to attach");
                        return null;
                    }

                    adopted = await tcs.Task.ConfigureAwait(false);
                }

                // Information on purpose: the e2e gate asserts from the file log that prefetch
                // took the QUIET path (and never the activating fallback).
                _logger.LogInformation("Created background tab quietly via CDP (no activation)");
                return adopted;
            }
            finally
            {
                ctx.Page -= OnPage;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Quiet background-tab creation failed; falling back to NewPageAsync");
            return null;
        }
    }

    /// <summary>
    /// Applies the fetch/prefetch pages' fixed viewport (workspace-v7g7) — the exact 1280x720
    /// the app rendered and extracted at when the CONTEXT owned the viewport, now re-applied per
    /// page because the context runs viewport-less so the OS window stays ours to place.
    /// </summary>
    private async Task ApplyFetchViewportAsync(IPage page)
    {
        try
        {
            await page.SetViewportSizeAsync(
                BrowserConfiguration.FetchViewportWidth, BrowserConfiguration.FetchViewportHeight).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not apply the fetch viewport (non-fatal; the page renders at window size)");
        }
    }

    /// <summary>
    /// Reveals the shell's page pane and marks the session docked. Shared by the
    /// toggle path and the lens-on-demand summon path. Returns null when no shell
    /// is attached (unattended host) or the pane did not reveal.
    /// </summary>
    private async Task<BrowserWindowState?> DockWindowAsync()
    {
        if (_disposed || _page == null)
        {
            return null;
        }

        if (_shell.IsConnected)
        {
            // Single-window shell: docking = revealing the page pane. Geometry, focus and
            // z-order are the shell's problem (reader-primary by construction), and the lens
            // renders at the REAL pane width — no phone-viewport emulation.
            if (!await _shell.SetPaneVisibleAsync(true).ConfigureAwait(false))
            {
                return null;
            }

            _isDocked = true;
            _userWantsDock = true;
            return BrowserWindowState.Docked;
        }

        // Unattended host (NullShellChannel): there is no pane to reveal.
        return null;
    }

    // workspace-v7mb: fired by Playwright when the headed window is closed or the tab
    // crashes. Clears only the visible-state flag — _userWantsDock is preserved so a
    // subsequent headed relaunch re-docks if the user was in docked mode.
    private void OnHeadedPageClosed(object? sender, IPage e)
    {
        _isDocked = false;
        _logger.LogDebug("Headed browser window closed/crashed — cleared docked state");
    }

    // workspace-qigc: lens tab closed (by the user or a context teardown) — drop the
    // reference so the next GetLensPageAsync recreates it.
    private void OnLensPageClosed(object? sender, IPage e)
    {
        _lensPage = null;
        _logger.LogDebug("Lens tab closed — will recreate on next use");
    }

    private async Task DisposeContextUnsafeAsync()
    {
        if (_attachedBrowser != null)
        {
            // Shell attach mode: DISCONNECT only. Closing pages/contexts over CDP would
            // destroy the shell's own panes (the visible lens included) — they belong to
            // the shell window, not to this client.
            _lensPage = null;
            _page = null;
            _context = null;
            try
            {
                await _attachedBrowser.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disconnecting from the shell browser during cleanup");
            }

            _attachedBrowser = null;
        }

        if (_lensPage != null)
        {
            try
            {
                await _lensPage.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing lens tab during cleanup");
            }

            _lensPage = null;
        }

        if (_page != null)
        {
            try
            {
                await _page.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing Playwright page during cleanup");
            }

            _page = null;
        }

        if (_context != null)
        {
            try
            {
                await _context.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing Playwright context during cleanup");
            }

            _context = null;
        }

        if (_playwright != null)
        {
            try
            {
                _playwright.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing Playwright during cleanup");
            }

            _playwright = null;
        }
    }
}
