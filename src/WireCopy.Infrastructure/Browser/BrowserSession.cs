// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WireCopy.Application.Interfaces;
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
    private readonly BrowserConfiguration _browserConfig;
    private readonly ILogger<BrowserSession> _logger;
    private readonly ICookieManager _cookieManager;
    private readonly string _userDataDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;

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
    private long _lastRefocusTicks;

    // workspace-fihe follow-up: 1 when RestoreWindowAsync brought the browser forward for a user
    // interaction and no cleanup has handed keyboard focus back yet. Consumed by the next
    // MinimizeWindowAsync (many restore flows have no dedicated cleanup and rely on the next
    // background hide) and cleared whenever a weJustActivatedBrowser refocus actually runs.
    private int _pendingInteractionHandback;

    // workspace-75ng: the terminal's frontmost-app bundle id, captured ONCE at the first
    // browser launch (while the terminal still owns focus) so RefocusTerminalAsync can
    // re-activate THAT exact app — terminal-agnostic, fixing Ghostty. Null until captured
    // or if capture failed; the refocus then falls back to the TERM_PROGRAM name map.
    private string? _terminalBundleId;

    // workspace-75ng.4: the terminal window's pre-dock bounds, captured when the sidecar
    // tiles the screen so dismiss can RESTORE the terminal to full size. Null when not tiled.
    private TerminalTiling.WindowRect? _terminalTileBounds;

    // workspace-9k27.6: the tile rect we last SET, so restore can detect whether
    // the user has since rearranged the terminal (their layout then wins).
    private TerminalTiling.WindowRect? _lastTerminalTileRect;

    // workspace-9k27.7: one-shot latch for the user-visible macOS permission notice
    // (0 = not shown). Browse-mode logs are file-only, so an osascript Automation/
    // Accessibility failure logged at Warning is invisible in the TUI without this.
    private int _permissionNoticeShown;

    public BrowserSession(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<BrowserSession> logger,
        ICookieManager cookieManager)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
        _cookieManager = cookieManager;

        // Sidecar mode starts in the configured state (workspace-exbz): when on, the
        // first headed launch docks beside the terminal; the dock toggle drops to the
        // immersive view by clearing this intent. Default OFF (workspace-75ng): the headed
        // window launches PARKED off-screen (never pops up / steals focus at startup) and
        // 'O' brings it on-screen to dock.
        _userWantsDock = _browserConfig.Sidecar;
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
    public Action<string>? PermissionNoticeSink { get; set; }

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

            _lensPage = await _context.NewPageAsync().ConfigureAwait(false);
            _lensPage.Close += OnLensPageClosed;
            _logger.LogDebug("Lens tab created");
            return _lensPage;
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

    /// <inheritdoc />
    /// <summary>
    /// workspace-9k27.7: public startup entry — Program.cs calls this BEFORE any
    /// browser/GUI work, while the terminal is guaranteed frontmost. The lazy
    /// call at first browser launch remains as a permission-granted-later retry,
    /// but can no longer be the FIRST capture (which recorded Slack/IDE if the
    /// user had switched apps before the first page load).
    /// </summary>
    public Task CaptureTerminalIdentityAsync() => CaptureTerminalBundleIdAsync();

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
        if (_disposed || _page == null)
        {
            return;
        }

        // workspace-fihe follow-up: several restore-for-interaction flows (manual login,
        // ':cred test', the podcast provider's bot-challenge) have NO dedicated cleanup call —
        // the window is eventually hidden by the NEXT background MinimizeWindowAsync. Record
        // that WE brought the browser forward so that next hide — whoever triggers it — hands
        // keyboard focus back instead of stranding it on the invisible parked browser. Safe
        // against the focus-war: the hand-back is still gated on OUR browser being frontmost.
        Interlocked.Exchange(ref _pendingInteractionHandback, 1);

        try
        {
            // Un-minimize via CDP first (BringToFrontAsync alone doesn't restore
            // a CDP-minimized window on macOS)
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Browser.getWindowForTarget returned no payload");
            var windowId = windowInfo.GetProperty("windowId").GetInt32();
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to un-minimize browser window via CDP (non-fatal)");
        }

        try
        {
            await _page.BringToFrontAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to bring browser window to front (non-fatal)");
        }
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

        // workspace-fihe follow-up: fold in any pending hand-back recorded by RestoreWindowAsync.
        // Restore-for-interaction flows without a dedicated cleanup rely on the NEXT hide (often a
        // background prefetch park) to return focus; consuming the latch here restores the pre-fihe
        // self-heal without reintroducing the focus-war (the refocus stays frontmost-gated).
        var handBack = weJustActivatedBrowser
            || Interlocked.Exchange(ref _pendingInteractionHandback, 0) == 1;

        // Sidecar mode (workspace-exbz): "get out of the way" never hides a dock the
        // user wants and never SUMMONS a window they closed (workspace-wo4q). One
        // exception (workspace-mctt): a window RESTORED for interaction (captcha /
        // manual login brings the fetch tab forward) returns to the wanted dock —
        // post-gate cleanup calls this exact method.
        if (_userWantsDock)
        {
            // workspace-fihe: re-dock (which brings the browser ON-SCREEN) ONLY on the
            // interaction-cleanup path that just had the browser forward. A BACKGROUND prefetch
            // call must never re-dock a parked window — raising the browser over the app the user
            // switched to is the focus-war via the re-dock path. Its parked window simply stays
            // parked until the user's next 'O' / dock action re-summons it.
            if (handBack
                && !_isDocked
                && await GetWindowStateAsync().ConfigureAwait(false) == "normal")
            {
                await DockWindowAsync().ConfigureAwait(false); // force-refocuses on success
            }
            else if (handBack)
            {
                // Already docked (or in a non-normal state): the interaction happened in the
                // docked window itself — e.g. the user clicked a captcha in the sidecar — so
                // no re-dock runs and nothing else hands focus back. Do it here, gated on the
                // browser actually being frontmost (workspace-fihe audit F2).
                await RefocusTerminalAsync(weJustActivatedBrowser: true).ConfigureAwait(false);
            }

            return;
        }

        // Non-sidecar park. Forward the intent: an interaction-cleanup park (handBack) returns
        // keyboard focus to the terminal (else it would strand on the now-invisible browser the
        // user was interacting with); a routine background park stays focus-neutral.
        await ParkWindowOffScreenAsync(handBack).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BrowserWindowState?> ToggleWindowDockAsync()
    {
        if (_disposed || _page == null)
        {
            return null;
        }

        if (_isDocked)
        {
            // The user explicitly un-docked: drop the sticky intent so a future headed
            // relaunch does NOT auto-re-dock against their wishes, then re-PARK the window
            // off-screen (workspace-75ng) — not OS-minimized — so it keeps rendering and
            // re-docks instantly with current content. Still reported as Minimized.
            //
            // workspace-fihe: dismiss hands focus back to the terminal (weJustActivatedBrowser:
            // true) because the user may have clicked into the docked live page, leaving focus on
            // the browser we are about to hide. The refocus is gated on the browser still being
            // frontmost, so it is a no-op when the terminal already has focus and never steals
            // focus from a foreign app the user switched to. (Routed straight through Park rather
            // than MinimizeWindowAsync, whose shared "get out of the way" callers must stay
            // focus-neutral.)
            _userWantsDock = false;
            await ParkWindowOffScreenAsync(weJustActivatedBrowser: true).ConfigureAwait(false);
            return BrowserWindowState.Minimized;
        }

        return await DockWindowAsync().ConfigureAwait(false);
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

            var page = await ctx.NewPageAsync().ConfigureAwait(false);

            // Tab etiquette: creating a tab activates it in a headed window — hand
            // the window straight back to the lens/fetch tab so prefetch never
            // steals the page the user is reading (workspace-wo4q).
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

            // workspace-9k27.6: give the terminal its full window back on ANY
            // exit while docked+tiled — DisposeContextUnsafeAsync never did, so
            // a clean quit left the user's terminal permanently shrunken.
            await RestoreTerminalTileAsync().ConfigureAwait(false);

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

    /// <summary>
    /// Reads the headed window's CDP windowState ("normal" / "minimized" / …), or
    /// null when unavailable. Used to distinguish a RESTORED window (re-dockable)
    /// from a minimized/closed one (leave it alone).
    /// </summary>
    private async Task<string?> GetWindowStateAsync()
    {
        if (_disposed || _page == null)
        {
            return null;
        }

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
            return windowInfo?.GetProperty("bounds").GetProperty("windowState").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read window state (non-fatal)");
            return null;
        }
    }

    // workspace-75ng: capture the frontmost GUI app's bundle id ONCE — at the first browser
    // launch, before the headed window can take focus — so refocus targets the real terminal
    // (Ghostty, Warp, anything) by id rather than guessing from TERM_PROGRAM. Best-effort and
    // idempotent: on failure the bundle id stays null and refocus falls back to the name map.
    private async Task CaptureTerminalBundleIdAsync()
    {
        // Retry on every launch until we actually capture an id (workspace-75ng): if Automation
        // permission was not granted at first launch, a later launch — once the user grants it —
        // still captures. A successful capture short-circuits all later attempts.
        if (!OperatingSystem.IsMacOS() || _terminalBundleId is not null)
        {
            return;
        }

        var result = await RunOsascriptAsync(TerminalRefocus.CaptureFrontmostBundleIdScript).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        if (result.Value.ExitCode != 0)
        {
            _logger.LogWarning(
                "Could not capture the terminal app for focus return (osascript exit {ExitCode}): {Error}. "
                + "Grant Automation permission: System Settings → Privacy & Security → Automation. "
                + "Falling back to the TERM_PROGRAM name map.",
                result.Value.ExitCode,
                result.Value.StdErr.Trim());
            NotifyPermissionIssueOnce();
            return;
        }

        var bundleId = result.Value.StdOut.Trim();
        if (TerminalRefocus.IsUsableBundleId(bundleId))
        {
            _terminalBundleId = bundleId;
            _logger.LogInformation("Captured terminal app for focus return: {BundleId}", bundleId);
        }
        else
        {
            _logger.LogDebug(
                "Frontmost-app capture returned an unusable value ('{Value}') — falling back to TERM_PROGRAM",
                bundleId);
        }
    }

    // workspace-fihe: reads the bundle id of the current frontmost GUI app so a refocus can tell
    // whether OUR browser still holds focus (hand it back to the terminal) or the user has moved
    // to a foreign app (leave them alone). Reuses the startup capture script; returns null on any
    // failure so the caller falls back to its best-effort intent.
    private async Task<string?> QueryFrontmostBundleIdAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var result = await RunOsascriptAsync(TerminalRefocus.CaptureFrontmostBundleIdScript).ConfigureAwait(false);
        if (result is not { ExitCode: 0 } r)
        {
            return null;
        }

        var id = r.StdOut.Trim();
        return TerminalRefocus.IsUsableBundleId(id) ? id : null;
    }

    // workspace-fihe: <paramref name="weJustActivatedBrowser"/> is true ONLY on paths where
    // WireCopy itself just raised/activated the browser window (dock / dismiss / launch-flash /
    // restore-for-interaction). Background prefetch parks the off-screen window without ever
    // showing it, so they pass false and never refocus — that unconditional re-activation on a
    // cadence was the reported focus-war that fought the user switching to another app.
    private async Task RefocusTerminalAsync(bool weJustActivatedBrowser, bool force = false)
    {
        // Any hand-back attempt satisfies a pending RestoreWindowAsync latch — clear it here (the
        // single consumer chokepoint) so a stale latch can't fire a second, later hand-back.
        if (weJustActivatedBrowser)
        {
            Interlocked.Exchange(ref _pendingInteractionHandback, 0);
        }

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // Fast path: a background park never brings the browser forward, so there is nothing to
        // hand back — skip without even querying the frontmost app.
        if (!weJustActivatedBrowser)
        {
            return;
        }

        // Only return focus while the frontmost app is still OURS. If the user has moved to a
        // foreign app (e.g. the Claude app) since we raised the browser, respect it — never yank
        // focus back. DecideRefocus is the single, unit-tested source of truth for this call.
        var frontmost = await QueryFrontmostBundleIdAsync().ConfigureAwait(false);
        if (!TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, frontmost, _terminalBundleId))
        {
            _logger.LogDebug(
                "Not returning focus to the terminal — frontmost app '{Frontmost}' is not WireCopy's "
                + "browser or terminal; leaving the user where they are (workspace-fihe)",
                frontmost);
            return;
        }

        // Debounce: skip if another refocus happened within the last 500ms — UNLESS forced.
        // The dock path forces a final refocus AFTER bringing the browser to front, so it must
        // win even if a park/dock refocus fired moments earlier (workspace-75ng focus-steal).
        // workspace-9k27.7: the timestamp is only stamped when a claim actually PROCEEDS —
        // the old Exchange-before-check meant a burst of skipped calls kept extending the
        // window and could starve non-forced refocus indefinitely. Claimed via
        // CompareExchange so two racing callers can't both take the same slot.
        if (!TerminalRefocus.TryClaimRefocusSlot(
                ref _lastRefocusTicks,
                DateTime.UtcNow.Ticks,
                force,
                TimeSpan.FromMilliseconds(500).Ticks))
        {
            return;
        }

        // Re-activate the EXACT terminal captured at startup (by bundle id), falling back to
        // the TERM_PROGRAM name map. Null when neither is known — we deliberately do NOT guess
        // Apple Terminal, which is what returned focus to the wrong app under Ghostty.
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        var script = TerminalRefocus.ResolveActivateScript(_terminalBundleId, termProgram);
        if (script is null)
        {
            _logger.LogDebug(
                "No terminal target to refocus (no captured bundle id; TERM_PROGRAM='{TermProgram}' unrecognised)",
                termProgram);
            return;
        }

        var result = await RunOsascriptAsync(script).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        if (result.Value.ExitCode != 0)
        {
            _logger.LogWarning(
                "osascript failed (exit {ExitCode}): {Error}. Check System Settings → Privacy & Security → Automation permissions.",
                result.Value.ExitCode,
                result.Value.StdErr.Trim());
            NotifyPermissionIssueOnce();
            return;
        }

        _logger.LogDebug(
            "Refocused terminal via {Target}",
            _terminalBundleId is not null ? $"bundle id {_terminalBundleId}" : $"TERM_PROGRAM={termProgram}");
    }

    /// <summary>
    /// workspace-9k27.7: surfaces ONE user-visible status line the first time an osascript
    /// call fails for permission-ish reasons. Every failure path already logs a Warning, but
    /// browse mode logs to file only, so without this the user just experiences "focus keeps
    /// landing on the wrong app" / "my terminal never resizes" with no visible explanation.
    /// The latch is only consumed when a sink is actually wired (the notice must not be
    /// swallowed by an early failure that happens before the orchestrator attaches the sink).
    /// </summary>
    private void NotifyPermissionIssueOnce()
    {
        if (PermissionNoticeSink is not { } sink)
        {
            return;
        }

        if (Interlocked.Exchange(ref _permissionNoticeShown, 1) != 0)
        {
            return;
        }

        try
        {
            sink("macOS automation blocked — focus return/window tiling degraded. "
                + "Grant access: System Settings → Privacy & Security → Automation + Accessibility");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Permission notice sink threw (non-fatal)");
        }
    }

    /// <summary>
    /// Runs an osascript expression and returns its exit code / stdout / stderr, or null if
    /// the process could not start or timed out. Args are passed via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// (no shell), so the AppleScript needs no shell-quoting. macOS-only callers; non-fatal.
    /// </summary>
    private async Task<(int ExitCode, string StdOut, string StdErr)?> RunOsascriptAsync(string script)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "osascript invocation failed — ensure /usr/bin/osascript exists and Automation permissions are granted (non-fatal)");
            NotifyPermissionIssueOnce();
            return null;
        }
    }

    // NEVER-HEADLESS POLICY (workspace-8ne3): no `headless` parameter — Chromium is ALWAYS launched headful.
    // Headless is bot-detected/blocked on this app's target sites (Cloudflare, NYT, macleans.ca), so it is
    // disabled unconditionally. On a display-less host the headful browser runs under a virtual display (the
    // `run` script provides Xvfb); it never silently degrades to headless.
    private async Task LaunchBrowserAsync()
    {
        // workspace-75ng: capture the terminal's bundle id NOW — before Playwright spawns the
        // headed browser — while the terminal is still the frontmost app, so the later
        // RefocusTerminalAsync re-activates the right app (fixes Ghostty). Runs once ever.
        await CaptureTerminalBundleIdAsync().ConfigureAwait(false);

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

                // workspace-75ng: spawn the headed window OFF-SCREEN so it never flashes at
                // Chromium's default on-screen position and never steals keyboard focus at
                // startup. It stays headful and renders fully (CDP extraction is window-
                // visibility-independent); 'O' brings it on-screen to dock.
                $"--window-position={_browserConfig.ParkCoordinate},{_browserConfig.ParkCoordinate}",

                // workspace-75ng: keep the off-screen/occluded window PAINTING so the live
                // page is current the instant the user docks it — otherwise Chromium throttles
                // hidden windows and the spotlight/screenshots go stale.
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
            };
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
                // Chrome/131 but actually running Chromium/145), and its real window size for the viewport.
                UserAgent = null,
                ViewportSize = null,
                IgnoreHTTPSErrors = true,
            });

            // Additional timeout guard — LaunchPersistentContextAsync's own Timeout may not
            // cover all hang scenarios (e.g., no display for headed mode, broken Xvfb)
            if (await Task.WhenAny(launchTask, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != launchTask)
            {
                throw new TimeoutException("Browser launch timed out after 30 seconds");
            }

            _context = await launchTask.ConfigureAwait(false);

            await _context.AddInitScriptAsync(@"
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
            ").ConfigureAwait(false);

            _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync().ConfigureAwait(false);

            // Inject stored cookies on every launch so mid-session updates
            // (e.g., Shift+I login) are picked up on browser restart.
            await InjectStoredCookiesAsync().ConfigureAwait(false);

            // The browser is always headful (never-headless policy), so the headed-window wiring always runs.
            // Reset the docked flag if THIS headed window is later closed/crashes so the persistent "docked"
            // affordance can't lie (workspace-v7mb).
            _page!.Close += OnHeadedPageClosed;

            // When the headed browser launches/reappears, re-dock it if the user was in docked
            // mode, otherwise PARK it off-screen (workspace-75ng) — the default. Parking (not
            // minimizing) keeps it rendering for an instant re-dock and never pops up or steals
            // focus. It is brought on-screen later only when the user presses 'O'.
            if (_userWantsDock)
            {
                await DockWindowAsync().ConfigureAwait(false);
            }
            else
            {
                // Launch parks the just-raised headed window off-screen: hand the launch-flash
                // focus back to the terminal (gated on the browser still being frontmost).
                await ParkWindowOffScreenAsync(weJustActivatedBrowser: true).ConfigureAwait(false);
            }
        }
        catch
        {
            // Clean up partial state so the next call starts fresh
            await DisposeContextUnsafeAsync().ConfigureAwait(false);
            throw;
        }
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
    /// Parks the headed window OFF-SCREEN (workspace-75ng): windowState=normal at a far
    /// off-screen position. Unlike OS-minimize, the window keeps painting (no occlusion
    /// throttle) so a later dock is instant with current content, and it never grabs
    /// keyboard focus. Clears the docked flag and refocuses the terminal afterwards.
    /// Assumes a live headed <see cref="_page"/>; swallows CDP failures (non-fatal).
    /// </summary>
    // workspace-fihe: <paramref name="weJustActivatedBrowser"/> is forwarded to the terminal
    // refocus. It is true only when this park follows WireCopy raising the browser (the
    // launch-flash park, or the dock-failure re-park), and false for the routine background /
    // dismiss parks that never showed the window — those must not re-activate the terminal.
    private async Task ParkWindowOffScreenAsync(bool weJustActivatedBrowser)
    {
        if (_disposed || _page == null)
        {
            return;
        }

        _isDocked = false;

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Browser.getWindowForTarget returned no payload");
            var windowId = windowInfo.GetProperty("windowId").GetInt32();

            // CDP forbids combining windowState with explicit bounds, so normalize the
            // window first (it may arrive minimized from an earlier state) in one call,
            // then move it off-screen in the next.
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
            }).ConfigureAwait(false);

            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["left"] = _browserConfig.ParkCoordinate,
                    ["top"] = _browserConfig.ParkCoordinate,
                },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to park browser window off-screen (non-fatal)");
        }

        // workspace-75ng.4: if the sidecar had tiled the screen, give the terminal its full
        // window back so dismissing returns to the terminal-only view.
        await RestoreTerminalTileAsync().ConfigureAwait(false);

        // Hand keyboard focus back to the terminal — but only when this park followed WireCopy
        // raising the browser (weJustActivatedBrowser); a routine background/dismiss park never
        // showed the window, so it must not re-activate the terminal (workspace-fihe).
        await RefocusTerminalAsync(weJustActivatedBrowser).ConfigureAwait(false);
    }

    // workspace-75ng.4: resize the terminal window to the given tile (the slice the docked
    // browser leaves free). macOS only; no-op without tiling enabled, a captured terminal
    // bundle id, or Accessibility permission — it degrades to the plain edge dock. Captures the
    // terminal's pre-dock bounds ONCE so RestoreTerminalTileAsync can put it back on dismiss.
    private async Task TileTerminalAsync(TerminalTiling.WindowRect terminalRect)
    {
        if (!_browserConfig.TileTerminalWithSidecar
            || !OperatingSystem.IsMacOS()
            || _terminalBundleId is null)
        {
            return;
        }

        // Capture the terminal's current bounds once (before the first resize of this dock
        // session) so dismiss restores exactly what the user had.
        if (_terminalTileBounds is null)
        {
            var getResult = await RunOsascriptAsync(
                TerminalTiling.BuildGetBoundsScript(_terminalBundleId)).ConfigureAwait(false);
            if (getResult is { ExitCode: 0 })
            {
                _terminalTileBounds = TerminalTiling.TryParseBounds(getResult.Value.StdOut);
            }

            if (_terminalTileBounds is null)
            {
                _logger.LogWarning(
                    "Could not read the terminal window bounds for side-by-side tiling — skipping the resize. "
                    + "Grant Accessibility permission: System Settings → Privacy & Security → Accessibility.");
                NotifyPermissionIssueOnce();
                return;
            }
        }

        var setResult = await RunOsascriptAsync(
            TerminalTiling.BuildSetBoundsScript(_terminalBundleId, terminalRect)).ConfigureAwait(false);
        if (setResult is { ExitCode: not 0 })
        {
            _logger.LogWarning(
                "Failed to tile the terminal window (osascript exit {ExitCode}): {Error}. "
                + "Grant Accessibility permission: System Settings → Privacy & Security → Accessibility.",
                setResult.Value.ExitCode,
                setResult.Value.StdErr.Trim());
            NotifyPermissionIssueOnce();
        }
        else
        {
            // workspace-9k27.6: persist the pre-dock bounds + the tile so a crash
            // while docked can restore the user's terminal on next launch.
            _lastTerminalTileRect = terminalRect;
            TerminalTileRecovery.Record(_terminalBundleId, _terminalTileBounds.Value, terminalRect, _logger);
        }
    }

    // workspace-75ng.4: restore the terminal to its pre-dock bounds captured by
    // TileTerminalAsync, then forget them. No-op when tiling never ran.
    private async Task RestoreTerminalTileAsync()
    {
        if (_terminalTileBounds is null || _terminalBundleId is null || !OperatingSystem.IsMacOS())
        {
            return;
        }

        var rect = _terminalTileBounds.Value;
        _terminalTileBounds = null;
        var tiled = _lastTerminalTileRect;
        _lastTerminalTileRect = null;

        // The tile set-bounds never succeeded — the terminal was never actually
        // resized, so "restoring" now would clobber a window we never touched.
        if (tiled is null)
        {
            TerminalTileRecovery.Clear(_logger);
            return;
        }

        // workspace-9k27.6: the restore script finds the window still sitting ON
        // the tile WE set and restores exactly that one — never `window 1`, which
        // in a multi-window terminal may be a different window, and never a window
        // the user has since moved/resized (their arrangement wins; no match = no-op).
        var result = await RunOsascriptAsync(TerminalTiling.BuildRestoreMatchedWindowScript(
            _terminalBundleId, tiled.Value, rect, TerminalTileRecovery.MatchTolerancePx)).ConfigureAwait(false);
        if (result is { ExitCode: 0 } r)
        {
            if (r.StdOut.Trim() == TerminalTiling.RestoreResultNoMatch)
            {
                _logger.LogInformation(
                    "Terminal was rearranged while docked — keeping the user's layout instead of restoring pre-dock bounds");
            }
        }
        else if (result is { ExitCode: not 0 })
        {
            _logger.LogWarning(
                "Failed to restore the terminal window after un-tiling (osascript exit {ExitCode}): {Error}",
                result.Value.ExitCode,
                result.Value.StdErr.Trim());
            NotifyPermissionIssueOnce();
        }

        // Restored (or attempted) — the crash-recovery record is no longer needed.
        TerminalTileRecovery.Clear(_logger);
    }

    /// <summary>
    /// Positions the headed window on the configured side/fraction of the available
    /// screen (default right half) and marks it docked. Shared by the toggle path and
    /// the lens-on-demand summon path. Assumes a live headed <see cref="_page"/>;
    /// returns null (and logs) if CDP positioning fails.
    /// </summary>
    private async Task<BrowserWindowState?> DockWindowAsync()
    {
        if (_disposed || _page == null)
        {
            return null;
        }

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Browser.getWindowForTarget returned no payload");
            var windowId = windowInfo.GetProperty("windowId").GetInt32();

            // Activate the LENS tab in the docked window when it exists — the sidecar shows the
            // lens, never the fetch tab (workspace-qigc). Fall back to the fetch page so the
            // pre-lens dock paths keep working. NOTE: this OS-activates the browser window
            // (steals focus); the forced refocus at the end takes it back.
            var front = _lensPage ?? _page;
            await front.BringToFrontAsync().ConfigureAwait(false);

            // workspace-75ng: place the docked window via SidecarDocker — anchor on-screen,
            // read a STABLE work area (defeats the phantom read that mis-placed it far-left /
            // off-screen), place flush, then re-place at the Chromium-CLAMPED width capped to a
            // phone-width fraction so it is never full-width or past the Dock. All the geometry
            // decisions are unit-tested cross-platform via the IDockWindowGeometry seam.
            var geo = new CdpDockWindowGeometry(_page, cdp, windowId, _browserConfig.DockSettleDelayMs);

            // workspace-9k27.8: dock onto the display the TERMINAL occupies, not
            // the primary. Probe the terminal window's position via osascript on
            // macOS; elsewhere or on failure, fall back to the primary origin.
            (int X, int Y)? anchor = null;
            if (OperatingSystem.IsMacOS() && _terminalBundleId is not null)
            {
                var boundsResult = await RunOsascriptAsync(
                    TerminalTiling.BuildGetBoundsScript(_terminalBundleId)).ConfigureAwait(false);
                if (boundsResult is { ExitCode: 0 }
                    && TerminalTiling.TryParseBounds(boundsResult.Value.StdOut) is { } termRect)
                {
                    anchor = (termRect.X, termRect.Y);
                }
            }

            var placement = await SidecarDocker.PlaceAsync(
                geo, _browserConfig.DockSide, _browserConfig.DockWidthPx, _browserConfig.DockFraction, _logger, anchor)
                .ConfigureAwait(false);

            if (placement is { } p)
            {
                // Phone-shaped lens (workspace-o5yf): pin the lens viewport to a mobile CSS width
                // so responsive sites collapse to one column. Sticky emulation — independent of
                // the window size — so it runs after placement using the placed height.
                if (_lensPage != null && _browserConfig.LensViewportWidth > 0)
                {
                    try
                    {
                        await _lensPage.SetViewportSizeAsync(
                            _browserConfig.LensViewportWidth,
                            Math.Max(600, p.Browser.Height - 110)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to set lens viewport (non-fatal)");
                    }
                }

                // workspace-75ng.4: side-by-side tiling — resize the terminal to the slice the
                // browser leaves free (computed from the placed browser width). Only on the dock
                // transition, never per spotlight update (avoids flicker).
                var terminalRect = SidecarGeometry.PlanTerminalTile(p.Display, _browserConfig.DockSide, p.Browser.Width);
                if (terminalRect is { } tr)
                {
                    await TileTerminalAsync(tr).ConfigureAwait(false);
                }
            }
            else
            {
                // workspace-9k27.9: placement failed/was skipped — the window was
                // already normalized and moved on-screen by the anchor step, so it
                // now sits at (0,0) over the terminal. Do NOT claim Docked (the
                // state must reflect the outcome, not the attempt): re-park it
                // off-screen and report failure so the caller/UI stays truthful.
                _logger.LogWarning("Sidecar placement returned no geometry — re-parking off-screen instead of claiming docked");

                // The park itself does not refocus (weJustActivatedBrowser: false); the forced
                // refocus below owns the hand-back after we brought the browser forward to dock.
                await ParkWindowOffScreenAsync(weJustActivatedBrowser: false).ConfigureAwait(false);
                await RefocusTerminalAsync(weJustActivatedBrowser: true, force: true).ConfigureAwait(false);
                return null;
            }

            _isDocked = true;
            _userWantsDock = true;

            // The sidecar is a companion view, not a focus target — hand keyboard focus back to
            // the terminal. Delay + FORCE so it wins the race against the BringToFront above
            // (which activated the browser window) — the workspace-75ng focus-steal fix.
            await Task.Delay(_browserConfig.DockRefocusDelayMs).ConfigureAwait(false);
            await RefocusTerminalAsync(weJustActivatedBrowser: true, force: true).ConfigureAwait(false);
            return BrowserWindowState.Docked;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dock browser window (non-fatal)");
            return null;
        }
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

    /// <summary>
    /// CDP-backed <see cref="IDockWindowGeometry"/> (workspace-75ng): the real implementation
    /// of the dock-placement seam. Reads <c>window.screen.avail*</c> off the fetch page and
    /// moves/sizes the window via <c>Browser.setWindowBounds</c>. The placement ALGORITHM lives
    /// in <see cref="SidecarDocker"/>/<see cref="SidecarGeometry"/> so it is testable without a
    /// browser or a Mac; this class is just the thin I/O adapter.
    /// </summary>
    private sealed class CdpDockWindowGeometry(IPage page, Microsoft.Playwright.ICDPSession cdp, int windowId, int settleDelayMs = 60)
        : IDockWindowGeometry
    {
        private readonly int _settleDelayMs = settleDelayMs;

        public async Task NormalizeAsync() =>
            await SetBoundsRawAsync(new Dictionary<string, object> { ["windowState"] = "normal" }).ConfigureAwait(false);

        public async Task MoveAsync(int left, int top) =>
            await SetBoundsRawAsync(new Dictionary<string, object> { ["left"] = left, ["top"] = top }).ConfigureAwait(false);

        public Task SettleAsync() => Task.Delay(_settleDelayMs);

        public async Task<SidecarGeometry.DisplayInfo?> ReadDisplayAsync()
        {
            try
            {
                var s = await page.EvaluateAsync<int[]>(
                    "() => [window.screen.availWidth, window.screen.availHeight, " +
                    "Math.round(window.screen.availLeft || 0), Math.round(window.screen.availTop || 0)]")
                    .ConfigureAwait(false);
                if (s is not { Length: >= 4 })
                {
                    return null;
                }

                return new SidecarGeometry.DisplayInfo(s[2], s[3], s[0], s[1]);
            }
            catch
            {
                return null;
            }
        }

        public async Task<TerminalTiling.WindowRect?> ReadWindowAsync()
        {
            try
            {
                var info = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
                if (info is null)
                {
                    return null;
                }

                var b = info.Value.GetProperty("bounds");
                return new TerminalTiling.WindowRect(
                    b.GetProperty("left").GetInt32(),
                    b.GetProperty("top").GetInt32(),
                    b.GetProperty("width").GetInt32(),
                    b.GetProperty("height").GetInt32());
            }
            catch
            {
                return null;
            }
        }

        public async Task SetWindowAsync(TerminalTiling.WindowRect rect) =>
            await SetBoundsRawAsync(new Dictionary<string, object>
            {
                ["left"] = rect.X,
                ["top"] = rect.Y,
                ["width"] = rect.Width,
                ["height"] = rect.Height,
            }).ConfigureAwait(false);

        private async Task SetBoundsRawAsync(Dictionary<string, object> bounds) =>
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = bounds,
            }).ConfigureAwait(false);
    }
}
