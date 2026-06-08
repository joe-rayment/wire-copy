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
    private readonly string _preloadUserDataDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _preloadLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private IBrowserContext? _preloadContext;
    private bool _pageIsHeadless;
    private bool _isDocked;
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
        _userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "browser-profile");
        _preloadUserDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "preload-profile");
    }

    /// <inheritdoc />
    // Reference-typed field reads are atomic in .NET; we don't need to take _lock
    // just to ask "is this currently non-null?" The reader may observe the prior
    // state if a launch/disposal is mid-flight, which is acceptable for this probe.
    public bool HasActiveBrowser => !_disposed && _page != null;

    /// <inheritdoc />
    public bool HasBrowserContext => !_disposed && _context != null;

    /// <inheritdoc />
    // The preload context is launched lazily on first use by
    // CreateBackgroundPageAsync (with its own 2s retry), so we must NOT require
    // _preloadContext to be pre-launched here — doing so would deadlock first use.
    // 'Reachable' = the session is not disposed.
    public bool HasPreloadContext => !_disposed;

    /// <inheritdoc />
    public bool IsBrowserAvailable => true;

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
        _logger.LogDebug("Warming up browser session (headless={Headless})", _browserConfig.Headless);
        await GetOrCreatePageAsync(_browserConfig.Headless).ConfigureAwait(false);
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
            await MinimizeWindowAsync().ConfigureAwait(false); // also clears _isDocked
            return BrowserWindowState.Minimized;
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

            // Right half of the available screen area (as the page sees it).
            var screen = await _page.EvaluateAsync<int[]>(
                "() => [window.screen.availWidth, window.screen.availHeight]").ConfigureAwait(false);
            var screenWidth = screen is { Length: > 0 } ? screen[0] : 1280;
            var screenHeight = screen is { Length: > 1 } ? screen[1] : 800;
            var halfWidth = screenWidth / 2;

            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["left"] = halfWidth,
                    ["top"] = 0,
                    ["width"] = screenWidth - halfWidth,
                    ["height"] = screenHeight,
                },
            }).ConfigureAwait(false);

            await _page.BringToFrontAsync().ConfigureAwait(false);
            _isDocked = true;
            return BrowserWindowState.Docked;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dock browser window right (non-fatal)");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _page == null)
            {
                return null;
            }

            try
            {
                return await _page.ScreenshotAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to capture screenshot (non-fatal)");
                return null;
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
            var ctx = await EnsurePreloadContextAsync().ConfigureAwait(false);
            if (ctx == null)
            {
                return null;
            }

            var page = await ctx.NewPageAsync().ConfigureAwait(false);
            _logger.LogDebug(
                "Created background page in preload context (total pages: {Count})",
                ctx.Pages.Count);
            return page;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create background page in preload context");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> SyncCookiesToPreloadContextAsync(IReadOnlyList<StoredCookie> cookies)
    {
        if (_disposed || cookies == null || cookies.Count == 0)
        {
            return 0;
        }

        await _preloadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Deliberately do NOT launch the preload context just to push cookies.
            // If preload hasn't started yet, the cookies will be picked up at
            // launch via InjectStoredCookiesAsync (which reads cookies.json).
            if (_preloadContext == null)
            {
                _logger.LogDebug(
                    "Preload context not yet launched — cookie sync deferred (will load from cookies.json on launch)");
                return 0;
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

            if (pwCookies.Count == 0)
            {
                return 0;
            }

            try
            {
                await _preloadContext.AddCookiesAsync(pwCookies).ConfigureAwait(false);
                _logger.LogDebug(
                    "Synced {Count} cookies into preload context",
                    pwCookies.Count);
                return pwCookies.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to sync cookies into preload context (non-fatal — will reload on next preload launch)");
                return 0;
            }
        }
        finally
        {
            _preloadLock.Release();
        }
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
            _preloadLock.Dispose();
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

    private async Task LaunchBrowserAsync(bool headless)
    {
        // Clean up any leftover state from a previous failed launch
        await DisposeContextUnsafeAsync().ConfigureAwait(false);

        Directory.CreateDirectory(_userDataDir);

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
            };
            if (OperatingSystem.IsLinux())
            {
                args.AddRange(["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]);
            }

            if (!headless)
            {
                args.Add("--window-size=800,600");
            }

            var launchTask = _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                Args = args.ToArray(),
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

            // Minimize headed browser so it stays in the background.
            // RestoreWindowAsync (BringToFront) is called when user interaction is needed.
            if (!headless)
            {
                await MinimizeWindowAsync().ConfigureAwait(false);
            }

            // Set mode flag only on success — if launch throws, the flag stays
            // stale so the next call correctly retries the mode switch.
            _pageIsHeadless = headless;
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
    /// Lazily launches a SECOND, always-headless Playwright persistent context
    /// at <c>{LocalAppData}/WireCopy/preload-profile</c>. This context is
    /// used exclusively for background pre-fetching so the user's foreground
    /// browser window and tab strip are never touched by preload activity.
    /// </summary>
    private async Task<IBrowserContext?> EnsurePreloadContextAsync()
    {
        if (_disposed)
        {
            return null;
        }

        if (_preloadContext != null)
        {
            return _preloadContext;
        }

        await _preloadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return null;
            }

            if (_preloadContext != null)
            {
                return _preloadContext;
            }

            // Reuse the already-launched Playwright instance from the foreground
            // context so we don't spin up a second node.js process. Falls back to
            // creating one if the foreground hasn't launched yet (e.g., a preload
            // is requested before the user opens any page).
            if (_playwright == null)
            {
                _logger.LogDebug(
                    "Preload context: launching standalone Playwright instance "
                    + "(foreground context not yet active)");
                var createTask = Playwright.CreateAsync();
                if (await Task.WhenAny(createTask, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != createTask)
                {
                    _logger.LogWarning("Preload Playwright.CreateAsync timed out");
                    return null;
                }

                _playwright = await createTask.ConfigureAwait(false);
            }

            Directory.CreateDirectory(_preloadUserDataDir);

            var args = new List<string>
            {
                "--disable-blink-features=AutomationControlled",
                "--disable-infobars",
            };
            if (OperatingSystem.IsLinux())
            {
                args.AddRange(["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]);
            }

            try
            {
                var launchTask = _playwright.Chromium.LaunchPersistentContextAsync(
                    _preloadUserDataDir,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        Headless = true,
                        Args = args.ToArray(),
                        Timeout = 30000,
                        UserAgent = _browserConfig.UserAgent,
                        ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
                        IgnoreHTTPSErrors = true,
                    });

                if (await Task.WhenAny(launchTask, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != launchTask)
                {
                    _logger.LogWarning("Preload context launch timed out");
                    return null;
                }

                _preloadContext = await launchTask.ConfigureAwait(false);

                await _preloadContext.AddInitScriptAsync(@"
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                    if (!window.chrome) window.chrome = {};
                    if (!window.chrome.runtime) window.chrome.runtime = {};
                    const originalQuery = window.navigator.permissions?.query;
                    if (originalQuery) {
                        window.navigator.permissions.query = (parameters) =>
                            parameters.name === 'notifications'
                                ? Promise.resolve({ state: Notification.permission })
                                : originalQuery(parameters);
                    }
                    if (navigator.plugins.length === 0) {
                        Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                    }
                    if (!navigator.languages || navigator.languages.length === 0) {
                        Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                    }
                ").ConfigureAwait(false);

                await InjectStoredCookiesIntoAsync(_preloadContext, "preload").ConfigureAwait(false);

                _logger.LogInformation(
                    "Preload context launched (headless, profile={Profile})",
                    _preloadUserDataDir);
                return _preloadContext;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to launch preload context");
                if (_preloadContext != null)
                {
                    try
                    {
                        await _preloadContext.CloseAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }

                    _preloadContext = null;
                }

                return null;
            }
        }
        finally
        {
            _preloadLock.Release();
        }
    }

    private async Task DisposeContextUnsafeAsync()
    {
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

        // Tear down the preload context too — it shares the Playwright handle
        // and must be closed before _playwright.Dispose() to avoid leaking the
        // Chromium child process.
        if (_preloadContext != null)
        {
            try
            {
                await _preloadContext.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing Playwright preload context during cleanup");
            }

            _preloadContext = null;
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
