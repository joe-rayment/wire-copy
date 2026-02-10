// Educational and personal use only.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Infrastructure.Configuration;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;

namespace NYTAudioScraper.Infrastructure.Browser;

/// <summary>
/// Manages a shared WebDriver instance that persists across page loads.
/// Lazily creates the driver on first use and handles driver crashes
/// by disposing the broken instance and creating a new one.
/// </summary>
public sealed class BrowserSession : IBrowserSession
{
    private readonly BrowserConfiguration _browserConfig;
    private readonly ILogger<BrowserSession> _logger;
    private readonly object _lock = new();
    private IWebDriver? _driver;
    private bool _disposed;

    public BrowserSession(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<BrowserSession> logger)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
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

            _logger.LogInformation("Creating new WebDriver session (headless={Headless})", headless);
            _driver = CreateWebDriver(headless);
            return _driver;
        }
    }

    public void ReleaseDriver()
    {
        // No-op: the session retains the driver for reuse.
        // The driver is only disposed when the session itself is disposed
        // or when a crash is detected.
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

        try
        {
            _driver.Quit();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error quitting WebDriver during cleanup");
        }

        try
        {
            _driver.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing WebDriver during cleanup");
        }

        _driver = null;
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
            options.AddArgument("--window-size=1400,900");
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
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(_browserConfig.PageLoadTimeoutSeconds);

        // Mask WebDriver detection
        ((IJavaScriptExecutor)driver).ExecuteScript(@"
            Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
            window.navigator.chrome = {runtime: {}};
        ");

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
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(_browserConfig.PageLoadTimeoutSeconds);

        return driver;
    }
}
