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
    private bool _pageIsHeadless;
    private bool _isDocked;

    // Sticky user intent (workspace-v7mb): distinct from _isDocked (the window's current
    // visible state). Set when the user docks, cleared when they explicitly minimize via
    // the toggle. Survives the auto-minimize at launch so a headed window that reappears
    // (e.g. after a headless→headed switch or a crash recovery) re-docks itself.
    private bool _userWantsDock;
    private bool _disposed;
    private bool _browsersInstalled;
    private long _lastRefocusTicks;

    public BrowserSession(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<BrowserSession> logger,
        ICookieManager cookieManager)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
        _cookieManager = cookieManager;

        // Sidecar mode starts in the configured state (workspace-exbz): when on, the
        // first headed launch docks beside the terminal instead of minimizing into the
        // void; the dock toggle drops to the immersive view by clearing this intent.
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
    public bool IsDocked => !_disposed && _isDocked && _page != null && !_pageIsHeadless;

    /// <inheritdoc />
    public bool WantsSidecar => !_disposed && _userWantsDock;

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
            if (_disposed || _context == null || _pageIsHeadless)
            {
                // The lens only exists beside a HEADED window; callers that need one
                // summon it first (SummonAndDockAsync → GetOrCreatePageAsync(false)).
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
    public async Task<IPage> GetOrCreatePageAsync(bool headless)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_page != null)
            {
                // Only recreate when switching headless→headed (needed for Shift+I interactive).
                // Headed→headless is skipped: a headed browser can serve headless requests fine,
                // and the relaunch costs 10-30 seconds that makes the app feel frozen.
                if (_pageIsHeadless && !headless)
                {
                    _logger.LogInformation(
                        "Switching headless→headed browser for interactive use");
                    await DisposeContextUnsafeAsync().ConfigureAwait(false);
                }
                else
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
            }

            _logger.LogInformation("Creating new Playwright browser session (headless={Headless})", headless);
            try
            {
                await LaunchBrowserAsync(headless).ConfigureAwait(false);
            }
            catch (PlaywrightException ex) when (!headless && LooksLikeMissingDisplay(ex.Message))
            {
                // workspace-j0b8: headed Chromium can't initialize when there's no X server
                // (CI / headless containers / SSH sessions without forwarding). Chromium exits
                // with "Missing X server or $DISPLAY" surfacing as a TargetClosedException —
                // the failure shape is indistinguishable from a real "target closed" so the
                // PageLoader retry path can't help. Auto-fall-back to headless once and log
                // a clear warning; this turns "Page navigated mid-load" (which is what the
                // user saw on macleans.ca in workspace-hrrf) into a successful page load on
                // any host that simply doesn't have a display attached.
                _logger.LogWarning(
                    "Headed browser launch failed — no X server / DISPLAY available. Falling back to headless mode for this session.");
                await LaunchBrowserAsync(headless: true).ConfigureAwait(false);
            }

            return _page!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task WarmUpAsync()
    {
        _logger.LogDebug("Warming up browser session (headless={Headless})", _browserConfig.EffectiveHeadless);
        await GetOrCreatePageAsync(_browserConfig.EffectiveHeadless).ConfigureAwait(false);
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
        if (_disposed || _page == null || _pageIsHeadless)
        {
            return;
        }

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
    public async Task MinimizeWindowAsync()
    {
        if (_disposed || _page == null || _pageIsHeadless)
        {
            return;
        }

        // Sidecar mode (workspace-exbz): "get out of the way" never hides a dock the
        // user wants and never SUMMONS a window they closed (workspace-wo4q). One
        // exception (workspace-mctt): a window RESTORED for interaction (captcha /
        // manual login brings the fetch tab forward) returns to the wanted dock —
        // post-gate cleanup calls this exact method.
        if (_userWantsDock)
        {
            if (!_isDocked && await GetWindowStateAsync().ConfigureAwait(false) == "normal")
            {
                await DockWindowAsync().ConfigureAwait(false);
            }

            return;
        }

        _isDocked = false;

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Browser.getWindowForTarget returned no payload");
            var windowId = windowInfo.GetProperty("windowId").GetInt32();
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "minimized" },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to minimize browser window (non-fatal)");
        }

        // Refocus the terminal app so keyboard input works immediately
        await RefocusTerminalAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BrowserWindowState?> ToggleWindowDockAsync()
    {
        if (_disposed || _page == null || _pageIsHeadless)
        {
            return null;
        }

        if (_isDocked)
        {
            // The user explicitly un-docked: drop the sticky intent so a future headed
            // relaunch does NOT auto-re-dock against their wishes.
            _userWantsDock = false;
            await MinimizeWindowAsync().ConfigureAwait(false); // also clears _isDocked
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
            // Ensure a HEADED window exists (switches headless→headed if a headless
            // page is currently serving the reader). The dedicated preload context is
            // untouched, so background prefetch keeps running on its own headless tabs.
            await GetOrCreatePageAsync(headless: false).ConfigureAwait(false);

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
            _logger.LogDebug(ex, "SummonAndDockAsync failed for {Url} (non-fatal)", url);
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
                await GetOrCreatePageAsync(_browserConfig.EffectiveHeadless).ConfigureAwait(false);
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
            if (!_pageIsHeadless && front != null)
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
        if (_disposed || _page == null || _pageIsHeadless)
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

    private async Task RefocusTerminalAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // Debounce: skip if another refocus happened within the last 500ms
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Exchange(ref _lastRefocusTicks, now);
        if (TimeSpan.FromTicks(now - previous).TotalMilliseconds < 500)
        {
            return;
        }

        try
        {
            // Detect terminal app from environment and re-activate it
            var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
            var appName = termProgram switch
            {
                "iTerm.app" => "iTerm2",
                "Apple_Terminal" => "Terminal",
                "WezTerm" => "WezTerm",
                "Alacritty" => "Alacritty",
                "kitty" => "kitty",
                _ => "Terminal", // fallback
            };

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                Arguments = $"-e 'tell application \"{appName}\" to activate'",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (process.HasExited && process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    _logger.LogWarning(
                        "osascript failed (exit {ExitCode}): {Error}. Check System Settings → Privacy → Automation permissions.",
                        process.ExitCode,
                        stderr.Trim());
                    return;
                }
            }

            _logger.LogDebug("Refocused terminal app: {AppName}", appName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to refocus terminal — ensure /usr/bin/osascript exists and Automation permissions are granted (non-fatal)");
        }
    }

    /// <summary>
    /// Resolves a pinned Chromium executable to launch directly (skipping the bundled
    /// Playwright browser install). Honours the explicit <c>WIRECOPY_CHROMIUM_EXECUTABLE</c>
    /// override first, then falls back to a chromium in the Playwright browsers cache
    /// (<c>PLAYWRIGHT_BROWSERS_PATH</c>) when present. Returns null to use Playwright's own
    /// managed download (the default).
    /// </summary>
    private static string? ResolveChromeExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("WIRECOPY_CHROMIUM_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(browsersPath) && Directory.Exists(browsersPath))
        {
            // The cache symlinks/dirs are named chromium-<rev>; pick a usable chrome binary.
            var link = Path.Combine(browsersPath, "chromium");
            if (File.Exists(link))
            {
                return link;
            }

            foreach (var dir in Directory.EnumerateDirectories(browsersPath, "chromium-*"))
            {
                var candidate = Path.Combine(dir, "chrome-linux", "chrome");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async Task LaunchBrowserAsync(bool headless)
    {
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

        // An operator can pin an existing Chromium build via WIRECOPY_CHROMIUM_EXECUTABLE
        // (e.g. a system package, or the host's pre-provisioned Playwright cache). When set we
        // launch that binary directly and SKIP the bundled-browser install — essential in locked-down
        // environments where the Playwright CDN is unreachable, and the path the web host uses.
        var chromeExecutable = ResolveChromeExecutable();

        // Ensure Playwright browsers are installed (idempotent — skips if already present).
        // Redirect stdout/stderr so install progress doesn't corrupt the TUI alternate screen.
        // Run with a timeout to prevent indefinite hangs on network issues.
        if (!_browsersInstalled && string.IsNullOrEmpty(chromeExecutable))
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
            };
            if (OperatingSystem.IsLinux())
            {
                args.AddRange(["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]);
            }

            if (!headless)
            {
                args.Add("--window-size=800,600");
            }

            if (!string.IsNullOrEmpty(chromeExecutable))
            {
                _logger.LogInformation("Using pinned Chromium executable: {Path}", chromeExecutable);
            }

            var launchTask = _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                Args = args.ToArray(),
                ExecutablePath = string.IsNullOrEmpty(chromeExecutable) ? null : chromeExecutable,
                Timeout = 30000, // 30s timeout prevents indefinite hang if browser can't start

                // Only override UserAgent for headless mode (HTTP fallback matching).
                // In headed mode, let Chromium use its real UA to avoid version mismatch
                // detection (e.g., claiming Chrome/131 but actually running Chromium/145).
                UserAgent = headless ? _browserConfig.UserAgent : null,
                ViewportSize = headless ? new ViewportSize { Width = 1400, Height = 900 } : null,
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

            // Set the mode flag BEFORE any window positioning below: Minimize/Dock guard
            // on _pageIsHeadless, and the failable launch work (install, context launch,
            // page creation, cookie injection) has already succeeded by this point. If
            // that earlier work throws, the flag stays stale so the next call retries the
            // mode switch; the positioning calls below swallow their own exceptions.
            _pageIsHeadless = headless;

            if (!headless)
            {
                // Reset the docked flag if THIS headed window is later closed/crashes so
                // the persistent "docked" affordance can't lie (workspace-v7mb).
                _page!.Close += OnHeadedPageClosed;

                // Re-dock automatically when the headed browser reappears if the user was
                // in docked mode; otherwise minimize it into the background.
                // RestoreWindowAsync (BringToFront) is called when interaction is needed.
                if (_userWantsDock)
                {
                    await DockWindowAsync().ConfigureAwait(false);
                }
                else
                {
                    await MinimizeWindowAsync().ConfigureAwait(false);
                }
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
    /// Positions the headed window on the configured side/fraction of the available
    /// screen (default right half) and marks it docked. Shared by the toggle path and
    /// the lens-on-demand summon path. Assumes a live headed <see cref="_page"/>;
    /// returns null (and logs) if CDP positioning fails.
    /// </summary>
    private async Task<BrowserWindowState?> DockWindowAsync()
    {
        if (_disposed || _page == null || _pageIsHeadless)
        {
            return null;
        }

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page).ConfigureAwait(false);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Browser.getWindowForTarget returned no payload");
            var windowId = windowInfo.GetProperty("windowId").GetInt32();

            // CDP forbids combining windowState with explicit bounds, so un-minimize
            // in one call before positioning in the next.
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
            }).ConfigureAwait(false);

            // Position on the configured side/fraction of the available screen area
            // (as the page sees it). Defaults to the right half (workspace-v7mb).
            // Read the work-area ORIGIN too (availLeft/availTop) so the dock lands on the
            // display the window actually sits on, not always the primary one
            // (workspace-nqqs). CDP setWindowBounds and screen.avail* share the same
            // virtual-screen coordinate space, so these compose correctly.
            var screen = await _page.EvaluateAsync<int[]>(
                "() => [window.screen.availWidth, window.screen.availHeight, " +
                "Math.round(window.screen.availLeft || 0), Math.round(window.screen.availTop || 0)]")
                .ConfigureAwait(false);
            var screenWidth = screen is { Length: > 0 } ? screen[0] : 1280;
            var screenHeight = screen is { Length: > 1 } ? screen[1] : 800;
            var availLeft = screen is { Length: > 2 } ? screen[2] : 0;
            var availTop = screen is { Length: > 3 } ? screen[3] : 0;
            var bounds = DockGeometry.Compute(
                screenWidth,
                screenHeight,
                availLeft,
                availTop,
                _browserConfig.DockSide,
                _browserConfig.DockFraction,
                _browserConfig.DockWidthPx);

            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["left"] = bounds.Left,
                    ["top"] = bounds.Top,
                    ["width"] = bounds.Width,
                    ["height"] = bounds.Height,
                },
            }).ConfigureAwait(false);

            // Activate the LENS tab in the docked window when it exists — the sidecar
            // shows the lens, never the fetch tab (workspace-qigc). Fall back to the
            // fetch page so the pre-lens dock paths keep working.
            var front = _lensPage ?? _page;
            await front.BringToFrontAsync().ConfigureAwait(false);

            // Phone-shaped lens (workspace-o5yf): pin the lens viewport to a mobile CSS
            // width so responsive sites collapse to one column and the spotlight's
            // targets are always on-screen. Height tracks the window minus chrome.
            if (_lensPage != null && _browserConfig.LensViewportWidth > 0)
            {
                try
                {
                    await _lensPage.SetViewportSizeAsync(
                        _browserConfig.LensViewportWidth,
                        Math.Max(600, bounds.Height - 110)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to set lens viewport (non-fatal)");
                }
            }

            _isDocked = true;
            _userWantsDock = true;

            // The sidecar is a companion view, not a focus target — hand keyboard
            // focus straight back to the terminal (macOS-only; no-op elsewhere).
            await RefocusTerminalAsync().ConfigureAwait(false);
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
}
