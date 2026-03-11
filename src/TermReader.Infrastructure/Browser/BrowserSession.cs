// Educational and personal use only.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Manages a shared WebDriver instance that persists across page loads.
/// Lazily creates the driver on first use and handles driver crashes
/// by disposing the broken instance and creating a new one.
/// </summary>
public sealed class BrowserSession : IBrowserSession
{
    private readonly BrowserConfiguration _browserConfig;
    private readonly ILogger<BrowserSession> _logger;
    private readonly ICookieManager _cookieManager;
    private readonly object _lock = new();
    private IWebDriver? _driver;
    private bool _driverIsHeadless;
    private int? _driverServicePid;
    private bool _disposed;

    public BrowserSession(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<BrowserSession> logger,
        ICookieManager cookieManager)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
        _cookieManager = cookieManager;
    }

    public bool HasActiveDriver
    {
        get
        {
            lock (_lock)
            {
                return _driver != null;
            }
        }
    }

    public IWebDriver GetOrCreateDriver(bool headless)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_driver != null)
            {
                // If the caller needs a different headless mode, dispose and recreate
                if (_driverIsHeadless != headless)
                {
                    _logger.LogInformation(
                        "Headless mode mismatch (current={Current}, requested={Requested}), recreating driver",
                        _driverIsHeadless,
                        headless);
                    DisposeDriverUnsafe();
                }
                else
                {
                    // Verify the existing driver is still alive
                    try
                    {
                        // Accessing the Title property is a lightweight check
                        _ = _driver.Title;
                        _logger.LogDebug("Reusing existing WebDriver session");
                        return _driver;
                    }
                    catch (WebDriverException ex)
                    {
                        _logger.LogWarning(ex, "Existing WebDriver session is dead, creating a new one");
                        DisposeDriverUnsafe();
                    }
                }
            }

            _logger.LogInformation("Creating new WebDriver session (headless={Headless})", headless);
            _driver = CreateWebDriver(headless);
            _driverIsHeadless = headless;
            InjectStoredCookies(_driver);
            return _driver;
        }
    }

    public Task WarmUpAsync()
    {
        _logger.LogDebug("Warming up browser session (headless={Headless})", _browserConfig.Headless);
        GetOrCreateDriver(_browserConfig.Headless);
        _logger.LogDebug("Browser session warm-up complete");
        return Task.CompletedTask;
    }

    public void ReleaseDriver()
    {
        // No-op: the session retains the driver for reuse.
        // The driver is only disposed when the session itself is disposed
        // or when a crash is detected.
    }

    public void RestoreWindow()
    {
        lock (_lock)
        {
            if (_disposed || _driver == null || _driverIsHeadless)
            {
                return;
            }

            try
            {
                _driver.Manage().Window.Size = new System.Drawing.Size(1400, 900);
                _driver.Manage().Window.Position = new System.Drawing.Point(0, 0);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to restore browser window (non-fatal)");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeDriverUnsafe();
        }
    }

    private void DisposeDriverUnsafe()
    {
        if (_driver == null)
        {
            return;
        }

        var needsForceKill = false;

        try
        {
            _driver.Quit();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error quitting WebDriver during cleanup");
            needsForceKill = true;
        }

        try
        {
            _driver.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing WebDriver during cleanup");
            needsForceKill = true;
        }

        _driver = null;

        if (needsForceKill)
        {
            ForceKillBrowserProcesses();
        }

        _driverServicePid = null;
    }

    private void ForceKillBrowserProcesses()
    {
        if (_driverServicePid == null)
        {
            return;
        }

        try
        {
            var process = Process.GetProcessById(_driverServicePid.Value);
            _logger.LogDebug("Force-killing driver process tree (PID={Pid})", _driverServicePid.Value);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error force-killing driver process (PID={Pid})", _driverServicePid.Value);
        }
    }

    private void InjectStoredCookies(IWebDriver driver)
    {
        try
        {
            var cookies = _cookieManager.LoadCookiesAsync().GetAwaiter().GetResult();
            if (cookies.Count == 0)
            {
                return;
            }

            // Group cookies by domain so we can navigate to each domain once
            var cookiesByDomain = cookies
                .GroupBy(c => c.Domain.TrimStart('.'))
                .ToList();

            var injectedCount = 0;

            foreach (var domainGroup in cookiesByDomain)
            {
                try
                {
                    // WebDriver requires being on the domain before setting cookies
                    driver.Navigate().GoToUrl($"https://{domainGroup.Key}");

                    foreach (var cookie in domainGroup)
                    {
                        try
                        {
                            var seleniumCookie = cookie.Expiry.HasValue
                                ? new OpenQA.Selenium.Cookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Expiry.Value)
                                : new OpenQA.Selenium.Cookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, null);

                            driver.Manage().Cookies.AddCookie(seleniumCookie);
                            injectedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to inject cookie {Name} for domain {Domain}", cookie.Name, cookie.Domain);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to navigate to domain {Domain} for cookie injection", domainGroup.Key);
                }
            }

            _logger.LogDebug("Injected {Count} stored cookies into WebDriver", injectedCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to inject stored cookies into WebDriver (non-fatal)");
        }
    }

    private IWebDriver CreateWebDriver(bool headless)
    {
        var browserType = _browserConfig.BrowserType.ToLowerInvariant();

        return browserType switch
        {
            "firefox" => CreateFirefoxDriver(headless),
            "chrome" => CreateChromeDriver(headless),
            _ => CreateChromeDriver(headless)
        };
    }

    private IWebDriver CreateChromeDriver(bool headless)
    {
        _logger.LogDebug("Creating Chrome driver with headless={Headless}", headless);
        var options = new ChromeOptions();

        // Anti-detection
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);
        options.AddArgument($"user-agent={_browserConfig.UserAgent}");

        // Suppress logging
        options.AddArgument("--log-level=3");
        options.AddArgument("--silent");

        if (headless)
        {
            options.AddArgument("--headless=new");
        }
        else
        {
            options.AddArgument("--window-size=800,600");
            options.AddArgument("--window-position=9999,9999");
        }

        if (_browserConfig.DisableImages)
        {
            options.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);
        }

        if (OperatingSystem.IsLinux())
        {
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--disable-gpu");
        }

        var service = ChromeDriverService.CreateDefaultService();
        service.SuppressInitialDiagnosticInformation = true;
        service.HideCommandPromptWindow = true;

        var driver = new ChromeDriver(service, options);
        _driverServicePid = service.ProcessId;
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(_browserConfig.PageLoadTimeoutSeconds);

        // Mask WebDriver detection
        ((IJavaScriptExecutor)driver).ExecuteScript(@"
            Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
            window.navigator.chrome = {runtime: {}};
        ");

        // Minimize headed browser so it doesn't cover the terminal.
        // Interactive refresh (Shift+I) will restore when user needs to interact.
        if (!headless)
        {
            try
            {
                driver.Manage().Window.Minimize();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to minimize browser window (non-fatal)");
            }
        }

        return driver;
    }

    private IWebDriver CreateFirefoxDriver(bool headless)
    {
        var options = new FirefoxOptions();

        // Anti-detection
        options.SetPreference("dom.webdriver.enabled", false);
        options.SetPreference("useAutomationExtension", false);
        options.SetPreference("general.useragent.override", _browserConfig.UserAgent);

        // Suppress logging
        options.LogLevel = FirefoxDriverLogLevel.Fatal;

        if (headless)
        {
            options.AddArgument("--headless");
        }
        else
        {
            options.AddArgument("--width=1400");
            options.AddArgument("--height=900");
        }

        if (_browserConfig.DisableImages)
        {
            options.SetPreference("permissions.default.image", 2);
        }

        var service = FirefoxDriverService.CreateDefaultService();
        service.SuppressInitialDiagnosticInformation = true;
        service.HideCommandPromptWindow = true;

        var driver = new FirefoxDriver(service, options);
        _driverServicePid = service.ProcessId;
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(_browserConfig.PageLoadTimeoutSeconds);

        return driver;
    }
}
