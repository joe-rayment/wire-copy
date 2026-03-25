// Educational and personal use only.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Manages a shared Playwright browser context and page that persists across page loads.
/// Lazily creates the browser on first use and handles session crashes
/// by disposing the broken instance and creating a new one.
/// Uses <c>LaunchPersistentContextAsync</c> to maintain cookies and local storage
/// across sessions via a user-data directory.
/// </summary>
public sealed class BrowserSession : IBrowserSession
{
    private readonly BrowserConfiguration _browserConfig;
    private readonly ILogger<BrowserSession> _logger;
    private readonly ICookieManager _cookieManager;
    private readonly string _userDataDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _pageIsHeadless;
    private bool _disposed;
    private bool _cookiesInjected;
    private bool _browsersInstalled;

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
            "TermReader",
            "browser-profile");
    }

    /// <inheritdoc />
    public bool HasActiveBrowser
    {
        get
        {
            try
            {
                _lock.Wait();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            try
            {
                return _page != null;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <inheritdoc />
    public bool IsBrowserAvailable => true;

    /// <inheritdoc />
    public async Task<IPage> GetOrCreatePageAsync(bool headless)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_page != null)
            {
                // If the caller needs a different headless mode, dispose and recreate
                if (_pageIsHeadless != headless)
                {
                    _logger.LogInformation(
                        "Headless mode mismatch (current={Current}, requested={Requested}), recreating browser",
                        _pageIsHeadless,
                        headless);
                    await DisposeContextUnsafeAsync();
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
                        await DisposeContextUnsafeAsync();
                    }
                }
            }

            _logger.LogInformation("Creating new Playwright browser session (headless={Headless})", headless);
            await LaunchBrowserAsync(headless);
            _pageIsHeadless = headless;
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
        // Always warm up in headless mode to avoid stealing terminal focus.
        // Headed mode is only used for interactive refresh (Shift+I).
        _logger.LogDebug("Warming up browser session (headless)");
        await GetOrCreatePageAsync(true);
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
    public async Task RestoreWindowAsync()
    {
        if (_disposed || _page == null || _pageIsHeadless)
        {
            return;
        }

        try
        {
            await _page.BringToFrontAsync();
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

        try
        {
            var cdp = await _page.Context.NewCDPSessionAsync(_page);
            var windowInfo = await cdp.SendAsync("Browser.getWindowForTarget");
            var windowId = windowInfo.Value.GetProperty("windowId").GetInt32();
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "minimized" },
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to minimize browser window (non-fatal)");
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_disposed || _page == null)
            {
                return null;
            }

            try
            {
                return await _page.ScreenshotAsync();
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
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _lock.Wait();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeContextUnsafeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during BrowserSession disposal");
        }
        finally
        {
            try
            {
                _lock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }

            _lock.Dispose();
        }
    }

    private async Task LaunchBrowserAsync(bool headless)
    {
        // Clean up any leftover state from a previous failed launch
        await DisposeContextUnsafeAsync();

        Directory.CreateDirectory(_userDataDir);

        // Ensure Playwright browsers are installed (idempotent — skips if already present).
        // Redirect stdout/stderr so install progress doesn't corrupt the TUI alternate screen.
        if (!_browsersInstalled)
        {
            _browsersInstalled = true;
            try
            {
                _logger.LogInformation("Ensuring Playwright browsers are installed...");
                var origOut = Console.Out;
                var origErr = Console.Error;
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                try
                {
                    var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                    if (exitCode != 0)
                    {
                        _logger.LogWarning("Playwright browser install returned exit code {ExitCode}", exitCode);
                    }
                }
                finally
                {
                    Console.SetOut(origOut);
                    Console.SetError(origErr);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playwright browser install failed (will attempt launch anyway)");
            }
        }

        try
        {
            _playwright = await Playwright.CreateAsync();

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

            _context = await _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                Args = args.ToArray(),

                // Only override UserAgent for headless mode (HTTP fallback matching).
                // In headed mode, let Chromium use its real UA to avoid version mismatch
                // detection (e.g., claiming Chrome/131 but actually running Chromium/145).
                UserAgent = headless ? _browserConfig.UserAgent : null,
                ViewportSize = headless ? new ViewportSize { Width = 1400, Height = 900 } : null,
                IgnoreHTTPSErrors = true,
            });

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
            ");

            _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();

            // Import stored cookies on first launch
            if (!_cookiesInjected)
            {
                _cookiesInjected = true;
                await InjectStoredCookiesAsync();
            }

            // Minimize headed browser so it stays in the background.
            // RestoreWindowAsync (BringToFront) is called when user interaction is needed.
            if (!headless)
            {
                await MinimizeWindowAsync();
            }
        }
        catch
        {
            // Clean up partial state so the next call starts fresh
            await DisposeContextUnsafeAsync();
            throw;
        }
    }

    private async Task InjectStoredCookiesAsync()
    {
        try
        {
            var cookies = await _cookieManager.LoadCookiesAsync();
            if (cookies.Count == 0)
            {
                return;
            }

            var pwCookies = cookies.Select(c => new Cookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = c.Path,
                Expires = c.Expiry.HasValue
                    ? new DateTimeOffset(c.Expiry.Value).ToUnixTimeSeconds()
                    : -1,
            }).ToList();

            await _context!.AddCookiesAsync(pwCookies);
            _logger.LogDebug("Injected {Count} stored cookies into Playwright context", pwCookies.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to inject stored cookies into Playwright context (non-fatal)");
        }
    }

    private async Task DisposeContextUnsafeAsync()
    {
        if (_page != null)
        {
            try
            {
                await _page.CloseAsync();
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
                await _context.CloseAsync();
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

        _cookiesInjected = false;
    }
}
