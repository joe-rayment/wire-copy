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

    private readonly BrowserConfiguration _browserConfig;
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

    // workspace-ynn9: true while OUR code has the window hidden/iconified (park/hide), false once
    // it is on-screen (docked) or brought forward for interaction. On non-macOS our hide reports
    // windowState=minimized — the SAME state a USER-minimize produces — so windowState alone can
    // no longer tell them apart. This intent flag lets the re-dock gate honor the workspace-wo4q
    // contract ("never summon a window the user minimized/closed") while still re-docking our own
    // parked window.
    private bool _iconifiedByUs;
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

    // workspace-ynn9: the terminal emulator's X11 window id (non-macOS), captured ONCE at the
    // first browser launch. On Linux the terminal is a separate X window, so after hiding/
    // iconifying the browser (which lets the WM reassign focus) RefocusTerminalAsync re-activates
    // THIS window via xdotool. Null until captured or when there is no X terminal window (bare
    // Xvfb / non-X / $WINDOWID unset and xdotool absent) — the refocus then no-ops cleanly.
    private string? _terminalX11WindowId;

    // workspace-ynn9.7: our headed browser's X11 window id, captured the first time we raise it
    // (BringToFront) while it is frontmost. The Linux focus hand-back uses it as the mirror of the
    // macOS frontmost-guard: hand focus back to the terminal only when the ACTIVE window is our
    // browser or the terminal — if a FOREIGN app has focus (the user switched away), respect it.
    private string? _browserX11WindowId;

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

    // workspace-v7g7: where the Corner park last placed the tile. While set, background parks
    // leave the window wherever it currently is — respecting a user drag/resize of the tile.
    // Every flow that intentionally moves the window on-screen (restore, dock) clears it so the
    // NEXT park re-places the corner.
    private TerminalTiling.WindowRect? _lastCornerRect;

    // workspace-v7g7: true once a park has OBSERVED the tile where we left it. Only then does a
    // later drift count as the user's drag/resize and get respected — a WM re-placing the window
    // as it maps during launch must be corrected, not adopted as "the user's spot".
    private bool _cornerRespectArmed;

    // workspace-v7g7: the display's REAL work area, captured at launch BEFORE the fetch page's
    // viewport emulation is applied — after it, JS screen metrics on the emulated pages report
    // the 1280x720 viewport instead of the display. Null when the capture failed; corner
    // placement then falls back to the (possibly lying) JS read.
    private SidecarGeometry.DisplayInfo? _realDisplay;

    // workspace-v7g7: 1 when RestoreWindowAsync found the window USER-minimized before summoning
    // it for an interaction — the cleanup park then returns it to minimized instead of a visible
    // park. Voided by docking (the user asked for the window on-screen).
    private int _restoreFoundUserMinimized;

    public BrowserSession(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<BrowserSession> logger,
        ICookieManager cookieManager,
        IShellChannel? shellChannel = null)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
        _cookieManager = cookieManager;
        _shell = shellChannel ?? new NullShellChannel();

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
    public bool IsShellAttached => !_disposed && _shell.IsConnected;

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

        if (_shell.IsConnected)
        {
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
            return;
        }

        // workspace-fihe follow-up: several restore-for-interaction flows (manual login,
        // ':cred test', the podcast provider's bot-challenge) have NO dedicated cleanup call —
        // the window is eventually hidden by the NEXT background MinimizeWindowAsync. Record
        // that WE brought the browser forward so that next hide — whoever triggers it — hands
        // keyboard focus back instead of stranding it on the invisible parked browser. Safe
        // against the focus-war: the hand-back is still gated on OUR browser being frontmost.
        Interlocked.Exchange(ref _pendingInteractionHandback, 1);

        // workspace-v7g7: if the USER had minimized the window (on macOS any minimize is theirs —
        // we never iconify there; elsewhere our own iconify is flagged), remember it so the
        // post-interaction cleanup park returns the window to THEIR minimized state instead of a
        // visible park. Read BEFORE the un-minimize below destroys the evidence.
        var priorState = await GetWindowStateAsync().ConfigureAwait(false);
        if (priorState == "minimized" && !_iconifiedByUs)
        {
            Interlocked.Exchange(ref _restoreFoundUserMinimized, 1);
        }

        // workspace-ynn9: the window is now brought ON-SCREEN for the user to interact with, so it
        // is no longer OUR-hidden. Clearing this means a subsequent USER-minimize of this on-screen
        // window is correctly treated as "leave it alone" by the re-dock gate (wo4q).
        _iconifiedByUs = false;

        // workspace-v7g7: the window is intentionally moving on-screen — the corner memory is
        // stale, so the next park re-places the tile instead of "respecting" this position.
        _lastCornerRect = null;
        _cornerRespectArmed = false;

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
            // workspace-ynn9: our truly-hidden park reports windowState=minimized on non-macOS (the
            // iconify that hides it under a real WM) — the SAME state a USER-minimize produces. So
            // windowState alone can't tell them apart; gate the "minimized is re-dockable" case on
            // _iconifiedByUs (WE hid it). macOS stays strict "normal". This preserves the
            // workspace-wo4q contract: a window the USER minimized/closed is left alone.
            var windowState = await GetWindowStateAsync().ConfigureAwait(false);
            var reDockable = OperatingSystem.IsMacOS()
                ? windowState == "normal"
                : windowState == "normal" || (windowState == "minimized" && _iconifiedByUs);
            if (handBack && !_isDocked && reDockable)
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
    public async Task<PaneOpenResult> OpenInPaneAsync(string url)
    {
        if (_disposed || !_shell.IsConnected || string.IsNullOrWhiteSpace(url))
        {
            return PaneOpenResult.NotAttached;
        }

        try
        {
            // Ensure the attach + lens exist, reveal the pane (sticky dock intent —
            // matches the user's explicit "show me this in the browser" gesture),
            // then drive the lens and hand the keyboard to the page.
            await GetOrCreatePageAsync().ConfigureAwait(false);
            var lens = await GetLensPageAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The shell lens page is unavailable");
            if (await DockWindowAsync().ConfigureAwait(false) != BrowserWindowState.Docked)
            {
                throw new InvalidOperationException("The shell pane did not reveal");
            }

            await lens.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000,
            }).ConfigureAwait(false);
            await _shell.SetModeAsync("browser").ConfigureAwait(false);
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
    // workspace-ynn9: <paramref name="linuxRaise"/> chooses the Linux hand-back primitive. On the
    // HIDE path the browser is gone, so RAISE + focus the terminal (windowactivate). On the DOCK
    // path the browser must stay visible beside the terminal, so focus WITHOUT raising
    // (windowfocus) — raising the un-tiled full-size terminal would cover the just-docked browser.
    private async Task RefocusTerminalAsync(bool weJustActivatedBrowser, bool force = false, bool linuxRaise = true)
    {
        // Any hand-back attempt satisfies a pending RestoreWindowAsync latch — clear it here (the
        // single consumer chokepoint) so a stale latch can't fire a second, later hand-back.
        if (weJustActivatedBrowser)
        {
            Interlocked.Exchange(ref _pendingInteractionHandback, 0);
        }

        // Fast path: a background park never brings the browser forward, so there is nothing to
        // hand back on any platform — skip without touching the window manager.
        if (!weJustActivatedBrowser)
        {
            return;
        }

        // workspace-ynn9: on Linux the terminal is a separate X window and hiding/iconifying the
        // browser lets the WM reassign focus, so explicitly hand focus back to the captured
        // terminal window. No-ops when no id was captured or xdotool/wmctrl is absent. The
        // frontmost-app guard and debounce below are macOS-specific (they rely on osascript).
        if (!OperatingSystem.IsMacOS())
        {
            await RefocusTerminalLinuxAsync(linuxRaise).ConfigureAwait(false);
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

    /// <summary>
    /// workspace-ynn9: runs a process (no shell) and returns exit code / stdout / stderr, or null
    /// if it could not start or timed out. Used for the Linux xdotool/wmctrl focus hand-back. Args
    /// go through <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> (no shell quoting).
    /// Non-fatal by contract — every caller treats null/non-zero as "feature unavailable here".
    /// </summary>
    private async Task<(int ExitCode, string StdOut, string StdErr)?> RunProcessCaptureAsync(
        string fileName, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // The tool is simply not installed (bare Xvfb, minimal container) — expected; debug only.
            _logger.LogDebug(ex, "Process '{FileName}' could not be run (non-fatal; feature unavailable here)", fileName);
            return null;
        }
    }

    // workspace-ynn9: capture the terminal emulator's X11 window id ONCE on Linux, before Playwright
    // spawns the browser while the terminal still owns focus. Prefer $WINDOWID (exported by xterm and
    // most X terminal emulators); fall back to `xdotool getactivewindow`. Leaves the id null (refocus
    // no-ops) when there is no X terminal window — bare Xvfb, a pty with no emulator, or no xdotool.
    private async Task CaptureTerminalX11WindowIdAsync()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || _terminalX11WindowId is not null)
        {
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return;
        }

        // Use $WINDOWID ONLY: it is exported by the terminal emulator itself, so it reliably names
        // the TERMINAL's window. A prior version fell back to `xdotool getactivewindow`, but that
        // returns whatever window currently holds focus — which, by the time this runs during
        // browser launch, may already be the browser or a launcher splash — and would then make the
        // focus hand-back raise the WRONG window (workspace-ynn9 review). No $WINDOWID → no capture
        // → the Linux hand-back cleanly no-ops.
        var fromEnv = Environment.GetEnvironmentVariable("WINDOWID");
        if (!string.IsNullOrWhiteSpace(fromEnv) && long.TryParse(fromEnv.Trim(), out var envId) && envId > 0)
        {
            _terminalX11WindowId = fromEnv.Trim();
            _logger.LogInformation("Captured terminal X11 window id {WindowId} for focus return (from $WINDOWID)", _terminalX11WindowId);
        }
        else
        {
            _logger.LogDebug("No $WINDOWID exported — Linux focus hand-back will no-op (bare Xvfb / terminal without $WINDOWID)");
        }
    }

    // workspace-ynn9.7: capture our headed browser's X11 window id while it is frontmost (call
    // right after BringToFront). Non-macOS + $WINDOWID-terminal known (else the hand-back no-ops
    // anyway, so there's nothing to guard). Captured once; the headed window id is stable.
    private async Task CaptureBrowserX11WindowIdAsync()
    {
        if (OperatingSystem.IsMacOS()
            || _browserX11WindowId is not null
            || _terminalX11WindowId is null
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return;
        }

        var result = await RunProcessCaptureAsync("xdotool", "getactivewindow").ConfigureAwait(false);
        if (result is { ExitCode: 0 } r && long.TryParse(r.StdOut.Trim(), out var id) && id > 0
            && r.StdOut.Trim() != _terminalX11WindowId)
        {
            _browserX11WindowId = r.StdOut.Trim();
            _logger.LogDebug("Captured headed browser X11 window id {WindowId} for the focus-handback guard", _browserX11WindowId);
        }
    }

    // workspace-ynn9: hand keyboard focus back to the captured terminal X window on Linux after the
    // browser was hidden or docked. raise=true (HIDE path, browser gone) uses `xdotool
    // windowactivate` to RAISE + focus the terminal; raise=false (DOCK path, browser must stay
    // visible) uses `xdotool windowfocus` to set input focus WITHOUT restacking, so the un-tiled
    // full-size terminal never covers the just-docked browser. No-ops when no id was captured or
    // xdotool is absent. workspace-ynn9.7: skips entirely when a FOREIGN window (not our browser or
    // terminal) holds focus, so the user switching apps in the hand-back window is respected.
    private async Task RefocusTerminalLinuxAsync(bool raise)
    {
        if (_terminalX11WindowId is null
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return;
        }

        // workspace-ynn9.7: mirror the macOS frontmost-guard — only hand focus back while the
        // ACTIVE window is OURS (the browser we just raised, or the terminal). If a FOREIGN window
        // holds focus, the user switched away in the hand-back window, so respect it and skip.
        // Unknown/zero active (focus loose after an iconify) is treated as ours → proceed.
        var active = await RunProcessCaptureAsync("xdotool", "getactivewindow").ConfigureAwait(false);
        var activeId = active is { ExitCode: 0 } ? active.Value.StdOut.Trim() : null;
        if (!string.IsNullOrEmpty(activeId)
            && activeId != "0"
            && activeId != _terminalX11WindowId
            && activeId != _browserX11WindowId)
        {
            _logger.LogDebug(
                "Skipping Linux focus hand-back — a foreign window ({Active}) holds focus, not our browser/terminal",
                activeId);
            return;
        }

        if (raise)
        {
            var activate = await RunProcessCaptureAsync("xdotool", "windowactivate", _terminalX11WindowId).ConfigureAwait(false);
            if (activate is { ExitCode: 0 })
            {
                _logger.LogDebug("Refocused terminal via xdotool windowactivate {WindowId}", _terminalX11WindowId);
                return;
            }

            var viaWmctrl = await RunProcessCaptureAsync("wmctrl", "-i", "-a", _terminalX11WindowId).ConfigureAwait(false);
            if (viaWmctrl is { ExitCode: 0 })
            {
                _logger.LogDebug("Refocused terminal via wmctrl -i -a {WindowId}", _terminalX11WindowId);
                return;
            }
        }
        else
        {
            // Focus WITHOUT raising — the docked browser must remain visible on top.
            var focus = await RunProcessCaptureAsync("xdotool", "windowfocus", _terminalX11WindowId).ConfigureAwait(false);
            if (focus is { ExitCode: 0 })
            {
                _logger.LogDebug("Focused terminal (no raise) via xdotool windowfocus {WindowId}", _terminalX11WindowId);
                return;
            }
        }

        _logger.LogDebug("Linux terminal refocus no-op (xdotool/wmctrl unavailable or window gone)");
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

        // workspace-75ng: capture the terminal's bundle id NOW — before Playwright spawns the
        // headed browser — while the terminal is still the frontmost app, so the later
        // RefocusTerminalAsync re-activates the right app (fixes Ghostty). Runs once ever.
        await CaptureTerminalBundleIdAsync().ConfigureAwait(false);

        // workspace-ynn9: the Linux equivalent — capture the terminal's X11 window id now, while
        // it still owns focus, so the browser-hide focus hand-back can re-activate it.
        await CaptureTerminalX11WindowIdAsync().ConfigureAwait(false);

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

            // workspace-75ng: spawn the headed window away from Chromium's default on-screen
            // position so the launch never flashes a big window over the user's work.
            // Corner mode (workspace-v7g7, macOS default): huge POSITIVE coordinates — an OS
            // that clamps (macOS pulls windows back on-screen; the old -32000 spawn clamped
            // into the field-reported top-LEFT sliver) lands it near the BOTTOM-RIGHT, where
            // the early/end-of-launch park then places the exact corner tile.
            // Offscreen mode keeps the classic macOS-only off-screen spawn (workspace-ynn9); on
            // a real non-macOS window manager either coordinate is clamped anyway and the early
            // HideAsync (which also iconifies) is what hides the window there.
            if (_browserConfig.EffectiveParkMode == ParkMode.Corner)
            {
                args.Add("--window-position=20000,20000");
            }
            else if (OperatingSystem.IsMacOS())
            {
                args.Add($"--window-position={_browserConfig.ParkCoordinate},{_browserConfig.ParkCoordinate}");
            }

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

            // workspace-v7g7: capture the REAL display work area from the still-unemulated first
            // page — the moment the fetch viewport is applied below, every JS screen metric on
            // this page reports the emulated 1280x720 instead of the display. Corner planning
            // uses this pre-emulation truth.
            _realDisplay = await CaptureRealDisplayAsync().ConfigureAwait(false);

            // workspace-v7g7: the context has NO Playwright-managed viewport (see the launch
            // options) — re-apply the exact viewport the app has always rendered and extracted
            // at (Playwright's old context default) to the fetch page itself.
            await ApplyFetchViewportAsync(_page).ConfigureAwait(false);

            // workspace-ynn9: on non-macOS the window can't be spawned off-screen (the WM clamps
            // --window-position back on-screen), so it maps at Chromium's default position for a
            // beat. Hide it as EARLY as possible — before the slower cookie/init setup below —
            // to shrink the on-screen flash to map→first-CDP. In Offscreen mode this is skipped
            // on macOS (already off-screen via the launch arg); in Corner mode (workspace-v7g7)
            // it runs everywhere — the launch arg only lands NEAR the corner (OS-clamped), so the
            // early park snaps the exact tile sooner. The end-of-launch dock/park below still
            // runs and owns the final state + focus hand-back.
            if (!OperatingSystem.IsMacOS() || _browserConfig.EffectiveParkMode == ParkMode.Corner)
            {
                await HideHeadedWindowEarlyAsync().ConfigureAwait(false);
            }

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

    // workspace-ynn9: minimal early hide used during launch on non-macOS to close the map→setup
    // flash window. Pure HideAsync — no tile-restore or focus hand-back (the end-of-launch
    // park/dock owns those). Swallows CDP failures (non-fatal): a failed early hide just means the
    // window stays visible a beat longer until the end-of-launch park hides it.
    private async Task HideHeadedWindowEarlyAsync()
    {
        if (_disposed || _page == null)
        {
            return;
        }

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Browser.getWindowForTarget returned no payload");
            var windowId = windowInfo.GetProperty("windowId").GetInt32();
            var geo = new CdpDockWindowGeometry(
                _page, cdp, windowId, _browserConfig.DockSettleDelayMs, _browserConfig.ParkCoordinate);
            await ApplyParkedStateAsync(geo).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Early headed-window hide failed (non-fatal)");
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
    /// The single "make the window not-in-the-way" actuator (workspace-v7g7), shared by the early
    /// launch hide and every runtime park. Reads the CURRENT window state and lets the pure
    /// <see cref="ParkPlanner"/> decide — the invariants live there: a USER-minimized window is
    /// never touched (the old unconditional normalize deminiaturized it twice per prefetched
    /// article — the field-reported "pops back up when I open a new site"), Corner mode places the
    /// intentional bottom-right tile, Offscreen keeps the classic hide.
    /// </summary>
    private async Task ApplyParkedStateAsync(IDockWindowGeometry geo)
    {
        var state = await geo.ReadWindowStateAsync().ConfigureAwait(false);
        var reMinimize = Interlocked.Exchange(ref _restoreFoundUserMinimized, 0) == 1;
        var decision = ParkPlanner.Decide(
            state, _iconifiedByUs, reMinimize, _browserConfig.EffectiveParkMode, OperatingSystem.IsMacOS());

        if (decision.NormalizeFirst)
        {
            await geo.NormalizeAsync().ConfigureAwait(false);
        }

        switch (decision.Action)
        {
            case ParkPlanner.ParkAction.LeaveMinimized:
                break;

            case ParkPlanner.ParkAction.ReMinimize:
                await geo.IconifyAsync().ConfigureAwait(false);
                break;

            case ParkPlanner.ParkAction.PlaceCorner:
                var placement = await CornerParker.PlaceAsync(
                    geo,
                    _browserConfig.CornerParkWidth,
                    _browserConfig.CornerParkHeight,
                    _browserConfig.CornerParkMargin,
                    _lastCornerRect,
                    _cornerRespectArmed,
                    _realDisplay,
                    _logger).ConfigureAwait(false);
                _lastCornerRect = placement?.Rect;
                _cornerRespectArmed = placement?.Settled ?? false;
                break;

            case ParkPlanner.ParkAction.HideOffscreen:
                await geo.HideAsync().ConfigureAwait(false);
                break;
        }

        _iconifiedByUs = decision.IconifiedByUsAfter;
    }

    /// <summary>
    /// Reads the display's REAL work area through the given page while it is still un-emulated
    /// (workspace-v7g7). Anchors the window on-screen once if the first read is phantom (a
    /// window spawned far off-screen can report no plausible display). Returns null on failure —
    /// callers fall back to the JS read.
    /// </summary>
    private async Task<SidecarGeometry.DisplayInfo?> CaptureRealDisplayAsync()
    {
        if (_page == null)
        {
            return null;
        }

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Browser.getWindowForTarget returned no payload");
            var windowId = windowInfo.GetProperty("windowId").GetInt32();
            var geo = new CdpDockWindowGeometry(
                _page, cdp, windowId, _browserConfig.DockSettleDelayMs, _browserConfig.ParkCoordinate);

            var display = await SidecarDocker.ReadStableDisplayAsync(geo).ConfigureAwait(false);
            if (display is null)
            {
                await geo.MoveAsync(0, 0).ConfigureAwait(false);
                display = await SidecarDocker.ReadStableDisplayAsync(geo).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Real display work area (pre-emulation): {Display}",
                display is { } d ? $"{d.AvailLeft},{d.AvailTop} {d.AvailWidth}x{d.AvailHeight}" : "unreadable");
            return display;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not capture the real display work area (non-fatal)");
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
    /// Parks the headed window out of the way (workspace-75ng/ynn9/v7g7), state-aware: a
    /// USER-minimized window is left untouched; Corner mode (macOS default) keeps an intentional
    /// bottom-right tile (windowState=normal, keeps painting, no focus grab); Offscreen mode does
    /// the classic off-screen move plus a non-macOS iconify (a real window manager clamps the
    /// off-screen coordinate back on-screen — the iconify is what actually removes it from view).
    /// Clears the docked flag, restores the terminal tile, and hands focus back. Assumes a live
    /// headed <see cref="_page"/>; swallows CDP failures (non-fatal).
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

            // The single runtime hide chokepoint (workspace-ynn9/v7g7): state-aware, so a
            // USER-minimized window is left alone, Corner mode (macOS default — it CLAMPS
            // off-screen coordinates into a stray sliver) places the intentional bottom-right
            // tile, and Offscreen keeps the classic off-screen move + non-macOS iconify.
            var geo = new CdpDockWindowGeometry(
                _page, cdp, windowId, _browserConfig.DockSettleDelayMs, _browserConfig.ParkCoordinate);
            await ApplyParkedStateAsync(geo).ConfigureAwait(false);
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

        // workspace-v7g7: docking is an explicit "window on-screen" request — void any pending
        // return-to-user-minimized intent, and drop the corner memory so the park after an
        // un-dock re-places the tile instead of "respecting" the dock position.
        Interlocked.Exchange(ref _restoreFoundUserMinimized, 0);
        _lastCornerRect = null;
        _cornerRespectArmed = false;

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

            // workspace-ynn9.7: the browser is frontmost right now — capture its X11 window id
            // (once) so the Linux focus hand-back can tell "our browser has focus" from "a foreign
            // app has focus". Non-macOS only; no-ops without xdotool / a captured terminal window.
            await CaptureBrowserX11WindowIdAsync().ConfigureAwait(false);

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

            // workspace-ynn9: on non-macOS the hide ICONIFIES the window; deiconify is not
            // guaranteed to have COMPOSITED the window by the time placement reads the work area
            // and re-shows it. Restore + wait until the page reports itself VISIBLE before
            // placement, so the window is mapped and its first on-screen frame is current (this
            // replaces the removed post-placement fresh-frame gate — same "ensure a live frame"
            // intent, but pre-placement, bounded, and non-fatal, with no CDP-session churn).
            await EnsureWindowRestoredForDockAsync(front, cdp, windowId).ConfigureAwait(false);

            var placement = await SidecarDocker.PlaceAsync(
                geo,
                _browserConfig.DockSide,
                _browserConfig.DockWidthPx,
                _browserConfig.DockFraction,
                _logger,
                anchor,
                displayOverride: _realDisplay)
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
            _iconifiedByUs = false; // the window is on-screen and docked now — no longer our-hidden

            // The sidecar is a companion view, not a focus target — hand keyboard focus back to
            // the terminal. Delay + FORCE so it wins the race against the BringToFront above
            // (which activated the browser window) — the workspace-75ng focus-steal fix.
            // workspace-ynn9: linuxRaise:false — on Linux focus the terminal WITHOUT raising it,
            // so the un-tiled full-size terminal window does not cover the just-docked browser.
            await Task.Delay(_browserConfig.DockRefocusDelayMs).ConfigureAwait(false);
            await RefocusTerminalAsync(weJustActivatedBrowser: true, force: true, linuxRaise: false).ConfigureAwait(false);
            return BrowserWindowState.Docked;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dock browser window (non-fatal)");
            return null;
        }
    }

    /// <summary>
    /// workspace-ynn9: before placement re-shows the window, make sure it is fully restored and
    /// COMPOSITED. The non-macOS hide iconifies the window, and a WM can deiconify lazily — so a
    /// re-dock could otherwise place/show a not-yet-mapped window and flash a stale/blank first
    /// frame. This normalizes and waits until the page reports itself visible (window mapped +
    /// composited). Best-effort and bounded: it never throws and never blocks the dock — a slow WM
    /// just means placement proceeds on the best state available, exactly as before this existed.
    /// Reuses the caller's CDP session (no per-iteration session churn) and does a plain JS
    /// visibility read (no CDP screenshot), so it adds no session leak or unbounded wait.
    /// </summary>
    private async Task EnsureWindowRestoredForDockAsync(IPage front, ICDPSession cdp, int windowId)
    {
        const int maxTries = 10;
        try
        {
            for (var i = 0; i < maxTries; i++)
            {
                // Deiconify (idempotent) — CDP forbids combining windowState with bounds, so this
                // is a standalone call; the window may already be normal.
                await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
                {
                    ["windowId"] = windowId,
                    ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
                }).ConfigureAwait(false);

                string? visibility = null;
                try
                {
                    visibility = await front.EvaluateAsync<string>("() => document.visibilityState").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "visibilityState read failed while restoring for dock (retrying)");
                }

                if (visibility == "visible")
                {
                    return;
                }

                await Task.Delay(_browserConfig.DockSettleDelayMs).ConfigureAwait(false);
            }

            _logger.LogDebug("Window did not report visible before dock placement after {Tries} tries — placing anyway", maxTries);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-dock window restore failed (non-fatal) — placement will proceed");
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

    /// <summary>
    /// CDP-backed <see cref="IDockWindowGeometry"/> (workspace-75ng): the real implementation
    /// of the dock-placement seam. Reads <c>window.screen.avail*</c> off the fetch page and
    /// moves/sizes the window via <c>Browser.setWindowBounds</c>. The placement ALGORITHM lives
    /// in <see cref="SidecarDocker"/>/<see cref="SidecarGeometry"/> so it is testable without a
    /// browser or a Mac; this class is just the thin I/O adapter.
    /// </summary>
    private sealed class CdpDockWindowGeometry(
        IPage page, Microsoft.Playwright.ICDPSession cdp, int windowId, int settleDelayMs = 60, int parkCoordinate = -32000)
        : IDockWindowGeometry
    {
        private readonly int _settleDelayMs = settleDelayMs;
        private readonly int _parkCoordinate = parkCoordinate;

        public async Task NormalizeAsync() =>
            await SetBoundsRawAsync(new Dictionary<string, object> { ["windowState"] = "normal" }).ConfigureAwait(false);

        public async Task IconifyAsync() =>
            await SetBoundsRawAsync(new Dictionary<string, object> { ["windowState"] = "minimized" }).ConfigureAwait(false);

        public async Task<string?> ReadWindowStateAsync()
        {
            try
            {
                var info = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
                return info?.GetProperty("bounds").GetProperty("windowState").GetString();
            }
            catch
            {
                return null;
            }
        }

        public async Task HideAsync()
        {
            // workspace-v7g7: NO normalize here — the caller (ParkPlanner) never routes a
            // minimized window into this hide (the old unconditional normalize deminiaturized
            // the user's own Cmd+M on every background prefetch park). CDP forbids combining
            // windowState with explicit bounds, hence the separate calls below.
            await SetBoundsRawAsync(new Dictionary<string, object>
            {
                ["left"] = _parkCoordinate,
                ["top"] = _parkCoordinate,
            }).ConfigureAwait(false);

            // On macOS the off-screen move alone hides it (huge virtual desktop) and keeps it
            // painting with no genie animation or focus juggle — the proven workspace-75ng park.
            // Everywhere else, a real window manager clamps the off-screen coordinate back
            // on-screen, so ALSO iconify: minimize is honored by the WM and truly removes the
            // window from view. Under bare Xvfb (no WM) the minimize no-ops harmlessly and the
            // move above is what hides it — so prod and tests exercise the same one primitive.
            if (!OperatingSystem.IsMacOS())
            {
                await SetBoundsRawAsync(new Dictionary<string, object> { ["windowState"] = "minimized" }).ConfigureAwait(false);
            }
        }

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
