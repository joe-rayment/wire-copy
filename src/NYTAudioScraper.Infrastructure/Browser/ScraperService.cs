// Educational and personal use only.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Configuration;
using NYTAudioScraper.Infrastructure.Parsing;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using OpenQA.Selenium.Support.UI;

namespace NYTAudioScraper.Infrastructure.Browser;

public class ScraperService : IScraperService
{
    private readonly NYTConfiguration _nytConfig;
    private readonly BrowserConfiguration _browserConfig;
    private readonly INYTAuthService _authService;
    private readonly IArticleParser _articleParser;
    private readonly IArticleCache _articleCache;
    private readonly ILogger<ScraperService> _logger;
    private string? _currentBrowserType;

    public ScraperService(
        IOptions<NYTConfiguration> nytConfig,
        IOptions<BrowserConfiguration> browserConfig,
        INYTAuthService authService,
        IArticleParser articleParser,
        IArticleCache articleCache,
        ILogger<ScraperService> logger)
    {
        _nytConfig = nytConfig.Value;
        _browserConfig = browserConfig.Value;
        _authService = authService;
        _articleParser = articleParser;
        _articleCache = articleCache;
        _logger = logger;
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Testing authentication");

        IWebDriver? driver = null;
        try
        {
            driver = CreateWebDriver();
            return await _authService.AuthenticateAsync(driver, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication test");
            return false;
        }
        finally
        {
            driver?.Dispose();
        }
    }

    public async Task<IEnumerable<Article>> ScrapeArticlesAsync(
        int maxArticles = 10,
        CancellationToken cancellationToken = default)
    {
        // Use default sections (The Front Page and Business)
        return await ScrapeArticlesBySectionsAsync(
            maxArticles,
            new[] { "The Front Page", "Business" },
            cancellationToken);
    }

    public async Task<Article?> ScrapeArticleByUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        // Check cache first
        var cachedArticle = await _articleCache.GetAsync(url, cancellationToken);
        if (cachedArticle != null)
        {
            _logger.LogInformation("📦 Cache HIT: {Title} (saved ~3s scraping time)", cachedArticle.Title);
            return cachedArticle;
        }

        _logger.LogDebug("Cache MISS: {Url}", url);
        _logger.LogInformation("Scraping article: {Url}", url);

        IWebDriver? driver = null;
        try
        {
            driver = CreateWebDriver();

            // Authenticate first
            var authenticated = await _authService.AuthenticateAsync(driver, cancellationToken);
            if (!authenticated)
            {
                _logger.LogWarning("Authentication failed, continuing without authentication");
            }

            // Navigate to article URL
            _logger.LogInformation("Navigating to {Url}", url);
#pragma warning disable S6966 // Selenium WebDriver 4.26.1 does not provide async navigation methods
            driver.Navigate().GoToUrl(url);
#pragma warning restore S6966
            await Task.Delay(_nytConfig.RateLimitDelayMs, cancellationToken);

            var (article, currentDriver) = await ParseWithRetryAsync(driver, url, cancellationToken);

            // Update driver reference in case it was swapped (e.g., switched to headed browser)
            driver = currentDriver;

            if (article != null)
            {
                _logger.LogInformation("Successfully scraped article: {Title}", article.Title);

                // Cache the article
                await _articleCache.SetAsync(url, article, cancellationToken: cancellationToken);
                _logger.LogInformation("📦 Cached article: {Title}", article.Title);
            }
            else
            {
                _logger.LogWarning("Failed to parse article after retries: {Url}", url);
            }

            return article;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scraping article {Url}", url);
            return null;
        }
        finally
        {
            driver?.Dispose();
        }
    }

    public async Task<IEnumerable<Article>> ScrapeArticlesBySectionsAsync(
        int maxArticles,
        string[] sections,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting scrape for up to {MaxArticles} articles from sections: {Sections}",
            maxArticles,
            string.Join(", ", sections));

        IWebDriver? driver = null;

        try
        {
            driver = CreateWebDriver();

            // Authenticate first
            var authenticated = await _authService.AuthenticateAsync(driver, cancellationToken);
            if (!authenticated)
            {
                _logger.LogWarning("Authentication failed, continuing without authentication");
            }

            // Navigate to Today's Paper
            var todaysPaperUrl = $"{_nytConfig.BaseUrl}/section/todayspaper";
            _logger.LogInformation("Navigating to {Url}", todaysPaperUrl);
#pragma warning disable S6966 // Selenium WebDriver 4.26.1 does not provide async navigation methods
            driver.Navigate().GoToUrl(todaysPaperUrl);
#pragma warning restore S6966

            // Wait for page to fully load - be patient!
            _logger.LogInformation("Waiting for page to fully load (this may take a while)...");
            await WaitForPageLoad(driver, cancellationToken);
            _logger.LogInformation("Page loaded successfully");

            // Extract article URLs from specified sections
            var articleUrls = ExtractArticleUrlsFromSections(driver, maxArticles, sections);
            _logger.LogInformation("Found {Count} article URLs from specified sections", articleUrls.Count);

            var articles = new List<Article>();

            // Scrape each article
            foreach (var url in articleUrls.Take(maxArticles))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    _logger.LogInformation("Scraping article: {Url}", url);
#pragma warning disable S6966 // Selenium WebDriver 4.26.1 does not provide async navigation methods
                    driver.Navigate().GoToUrl(url);
#pragma warning restore S6966

                    // Human-like behavior: random delay 5-12 seconds
                    var humanDelay = Random.Shared.Next(5000, 12000);

                    // 20% chance of extended "reading time" (simulates actually reading an article)
                    if (Random.Shared.NextDouble() < 0.2)
                    {
                        var extraDelay = Random.Shared.Next(10000, 25000);
                        humanDelay += extraDelay;
                        _logger.LogDebug("Extended reading pause ({ExtraDelay}ms)", extraDelay);
                    }

                    _logger.LogDebug("Waiting {DelayMs}ms before parsing (simulating reading)", humanDelay);
                    await Task.Delay(humanDelay, cancellationToken);

                    // Simulate realistic scrolling behavior with variation
                    await SimulateHumanScrolling(driver, cancellationToken);

                    var (article, currentDriver) = await ParseWithRetryAsync(driver, url, cancellationToken);

                    // Update driver reference in case it was swapped (e.g., switched to headed browser)
                    driver = currentDriver;

                    if (article != null)
                    {
                        articles.Add(article);
                        _logger.LogInformation("Successfully scraped article: {Title}", article.Title);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse article after retries: {Url}", url);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scraping article {Url}", url);
                }
            }

            _logger.LogInformation("Scraping completed. Successfully scraped {Count} articles", articles.Count);
            return articles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during scraping");
            return Enumerable.Empty<Article>();
        }
        finally
        {
            driver?.Dispose();
        }
    }

    private static bool DetectChallengePage(string pageSource)
    {
        var lowerSource = pageSource.ToLowerInvariant();
        var challengeIndicators = new[]
        {
            "verify you are human",
            "checking your browser",
            "datadome",
            "captcha",
            "please wait",
            "access denied",
            "enable javascript",
            "one more step",
            "security check",
            "challenge-container"
        };
        return Array.Exists(challengeIndicators, indicator => lowerSource.Contains(indicator));
    }

    private IWebDriver CreateWebDriver(bool useFallback = false)
    {
        var browserType = useFallback
            ? _browserConfig.FallbackBrowserType
            : (_currentBrowserType ?? _browserConfig.BrowserType);

        var browserTypeLower = browserType.ToLowerInvariant();
        _currentBrowserType = browserType;

        _logger.LogInformation("Creating WebDriver for browser: {BrowserType}{Fallback}",
            browserType,
            useFallback ? " (fallback)" : string.Empty);

        return browserTypeLower switch
        {
            "firefox" => CreateFirefoxDriver(),
            "chrome" => CreateChromeDriver(),
            _ => throw new InvalidOperationException($"Unsupported browser type: {browserType}. Supported types: Chrome, Firefox")
        };
    }

    private async Task WaitForPageLoad(IWebDriver driver, CancellationToken cancellationToken)
    {
        try
        {
            // Wait for document.readyState to be 'complete'
            var wait = new WebDriverWait(driver, TimeSpan.FromMinutes(5));

            _logger.LogDebug("Waiting for document.readyState = 'complete'...");
            wait.Until(d =>
            {
                var readyState = ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString();
                return readyState == "complete";
            });

            // Give it an extra moment for any dynamic content to settle
            _logger.LogDebug("Document ready, waiting for content to settle...");
            await Task.Delay(5000, cancellationToken);

            // Log page info for debugging
            var pageSource = driver.PageSource;
            var url = driver.Url;
            _logger.LogDebug("Page loaded: URL={Url}, PageLength={Length} chars", url, pageSource.Length);
        }
        catch (WebDriverTimeoutException ex)
        {
            _logger.LogWarning(ex, "Timeout waiting for page load, but continuing anyway");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for page load, but continuing anyway");
        }
    }

    private async Task<(Article? Article, IWebDriver Driver)> ParseWithRetryAsync(
        IWebDriver driver,
        string url,
        CancellationToken cancellationToken,
        int maxRetries = 3)
    {
        Article? article = null;
        var currentDriver = driver;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            var pageSource = currentDriver.PageSource;

            // Check for challenge page before parsing
            if (DetectChallengePage(pageSource))
            {
                _logger.LogWarning(
                    "Challenge page detected on attempt {Attempt} for {Url}.",
                    attempt,
                    url);

                // Offer interactive challenge completion - switch to headed browser
                if (_browserConfig.Headless && attempt == 1)
                {
                    var (success, newDriver) = await PromptForManualChallengeCompletionAsync(currentDriver, url, cancellationToken);
                    if (success && newDriver != null)
                    {
                        // Switch to the headed driver and retry
                        currentDriver = newDriver;
                        continue;
                    }
                }

                if (attempt < maxRetries)
                {
                    // Much longer backoff for challenge pages: 30-60 seconds
                    var challengeBackoff = Random.Shared.Next(30000, 60000);
                    _logger.LogInformation("Challenge backoff: waiting {Delay}s before retry", challengeBackoff / 1000);
                    await Task.Delay(challengeBackoff, cancellationToken);

                    // Refresh and try again
                    try
                    {
#pragma warning disable S6966
                        currentDriver.Navigate().Refresh();
#pragma warning restore S6966
                        await Task.Delay(Random.Shared.Next(5000, 10000), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error during challenge page refresh");
                    }
                }

                continue;
            }

            article = _articleParser.ParseArticle(pageSource, url);

            if (article != null)
            {
                if (attempt > 1)
                {
                    _logger.LogInformation("Successfully parsed article on attempt {Attempt}", attempt);
                }

                return (article, currentDriver);
            }

            if (attempt < maxRetries)
            {
                // Wait progressively longer between retries
                var retryDelay = (attempt * 3000) + Random.Shared.Next(1000, 3000);
                _logger.LogDebug(
                    "Parse attempt {Attempt} failed for {Url}, waiting {Delay}ms before retry",
                    attempt,
                    url,
                    retryDelay);

                await Task.Delay(retryDelay, cancellationToken);

                // Try refreshing the page on retry
                try
                {
                    _logger.LogDebug("Refreshing page for retry attempt {Attempt}", attempt + 1);
#pragma warning disable S6966
                    currentDriver.Navigate().Refresh();
#pragma warning restore S6966

                    // Wait for content to load with some variation
                    await Task.Delay(Random.Shared.Next(4000, 7000), cancellationToken);

                    // Scroll to trigger any lazy-loaded content
                    await SimulateHumanScrolling(currentDriver, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during page refresh (non-critical)");
                }
            }
        }

        return (article, currentDriver);
    }

    private async Task SimulateHumanScrolling(IWebDriver driver, CancellationToken cancellationToken)
    {
        try
        {
            // Variable number of scroll actions (1-4)
            var scrollCount = Random.Shared.Next(1, 5);

            for (int i = 0; i < scrollCount; i++)
            {
                // Random scroll position
                var scrollPosition = Random.Shared.Next(200, 800);
                ((IJavaScriptExecutor)driver).ExecuteScript($"window.scrollTo(0, {scrollPosition * (i + 1)});");

                // Random pause between scrolls (500ms - 2500ms)
                await Task.Delay(Random.Shared.Next(500, 2500), cancellationToken);

                // 30% chance of a small scroll back up (human-like)
                if (Random.Shared.NextDouble() < 0.3)
                {
                    var scrollBack = Random.Shared.Next(50, 200);
                    ((IJavaScriptExecutor)driver).ExecuteScript($"window.scrollBy(0, -{scrollBack});");
                    await Task.Delay(Random.Shared.Next(300, 800), cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during scroll simulation (non-critical)");
        }
    }

    private IWebDriver CreateChromeDriver()
    {
        var options = new ChromeOptions();

        // Anti-detection measures
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);
        options.AddArgument($"user-agent={_browserConfig.UserAgent}");

        // Suppress Chrome logging
        options.AddArgument("--log-level=3"); // Only fatal errors
        options.AddArgument("--silent");

        if (_browserConfig.Headless)
        {
            options.AddArgument("--headless=new");
            _logger.LogDebug("Running Chrome in headless mode");
        }
        else
        {
            options.AddArgument("--window-size=1400,900");
            _logger.LogDebug("Running Chrome in headed mode");
        }

        if (_browserConfig.DisableImages)
        {
            options.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);
        }

        // Platform-specific flags (avoid flags that crash on macOS)
        if (OperatingSystem.IsLinux())
        {
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--disable-gpu");
        }

        // Create ChromeDriver service to suppress its output
        var service = ChromeDriverService.CreateDefaultService();
        service.SuppressInitialDiagnosticInformation = true;
        service.HideCommandPromptWindow = true;

        var driver = new ChromeDriver(service, options);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(_browserConfig.PageLoadTimeoutSeconds);

        // Execute script to mask WebDriver detection
        var script = @"
            Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
            window.navigator.chrome = {runtime: {}};
        ";
        ((IJavaScriptExecutor)driver).ExecuteScript(script);

        _logger.LogDebug("Chrome driver created successfully");
        return driver;
    }

    private IWebDriver CreateFirefoxDriver()
    {
        var options = new FirefoxOptions();

        // Anti-detection measures for Firefox
        options.SetPreference("dom.webdriver.enabled", false);
        options.SetPreference("useAutomationExtension", false);
        options.SetPreference("general.useragent.override", _browserConfig.UserAgent);

        // Suppress ALL console output from Firefox
        options.SetPreference("devtools.console.stdout.content", false);
        options.SetPreference("browser.dom.window.dump.enabled", false);
        options.SetPreference("devtools.console.stderr.chrome", false);
        options.SetPreference("browser.chrome.toolbar_tips", false);
        options.SetPreference("browser.chrome.favicons", false);

        // Additional logging suppression
        options.SetPreference("extensions.logging.enabled", false);
        options.SetPreference("network.cookie.cookieBehavior", 1); // Block third-party cookies to reduce noise
        options.SetPreference("browser.safebrowsing.enabled", false);
        options.SetPreference("browser.safebrowsing.malware.enabled", false);

        // Suppress console errors from Firefox internals
        options.SetPreference("devtools.console.log.level", "off");
        options.SetPreference("browser.console.level", "off");
        options.SetPreference("logging.console.enabled", false);

        if (_browserConfig.Headless)
        {
            options.AddArgument("--headless");
            _logger.LogDebug("Running Firefox in headless mode");
        }
        else
        {
            options.AddArgument("--width=1400");
            options.AddArgument("--height=900");
            _logger.LogDebug("Running Firefox in headed mode");
        }

        if (_browserConfig.DisableImages)
        {
            options.SetPreference("permissions.default.image", 2);
        }

        // Disable automation indicators
        options.SetPreference("marionette", true);
        options.SetPreference("dom.webnotifications.enabled", false);

        // Suppress Firefox and geckodriver logging completely
        options.LogLevel = FirefoxDriverLogLevel.Fatal;

        // Create geckodriver service to suppress its output
        var service = FirefoxDriverService.CreateDefaultService();
        service.SuppressInitialDiagnosticInformation = true;
        service.HideCommandPromptWindow = true;

        // Set environment variables to suppress Firefox logging
        Environment.SetEnvironmentVariable("MOZ_LOG", string.Empty);

        // Use platform-specific null device: "NUL" on Windows, "/dev/null" on Unix
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        Environment.SetEnvironmentVariable("MOZ_LOG_FILE", nullDevice);
        Environment.SetEnvironmentVariable("NSPR_LOG_MODULES", string.Empty);

        // Redirect stderr to null to suppress console.error/console.warn from Firefox
        var originalError = Console.Error;
        try
        {
            Console.SetError(System.IO.TextWriter.Null);
            var driver = new FirefoxDriver(service, options);

            driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(_browserConfig.PageLoadTimeoutSeconds);

            // Execute script to mask WebDriver detection
            var script = @"
                Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
            ";
            ((IJavaScriptExecutor)driver).ExecuteScript(script);

            _logger.LogDebug("Firefox driver created successfully");
            return driver;
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private List<string> ExtractArticleUrlsFromSections(IWebDriver driver, int maxArticles, string[] targetSections)
    {
        try
        {
            _logger.LogInformation("Extracting article URLs from sections: {Sections}",
                string.Join(", ", targetSections));

            var articleUrls = new List<string>();
            var currentYear = DateTime.UtcNow.Year;
            var previousYear = currentYear - 1;

            // Find all sections on the page
            var sections = driver.FindElements(By.CssSelector("section, div[data-testid*='section'], div.css-13y0vf2"));

            foreach (var section in sections)
            {
                try
                {
                    // Try to find the section header
                    var headers = section.FindElements(By.CssSelector("h2, h3, [data-testid='section-heading']"));

                    if (headers.Count == 0)
                    {
                        continue;
                    }

                    var sectionTitle = headers[0].Text.Trim();

                    // Log all sections found for debugging
                    _logger.LogDebug("Found section: {SectionTitle}", sectionTitle);

                    // Check if this section matches any of the target sections
                    bool isTargetSection = Array.Exists(targetSections, targetSection =>
                        sectionTitle.Equals(targetSection, StringComparison.OrdinalIgnoreCase) ||
                        sectionTitle.Contains(targetSection, StringComparison.OrdinalIgnoreCase));

                    if (isTargetSection)
                    {
                        _logger.LogInformation("Processing section: {SectionTitle}", sectionTitle);

                        // Find all article links in this section with dynamic year detection
                        var links = section.FindElements(By.CssSelector($"a[href*='/{currentYear}/'], a[href*='/{previousYear}/']"))
                            .Select(e => e.GetAttribute("href"))
                            .Where(url => !string.IsNullOrWhiteSpace(url) &&
                                        (url.Contains("/article/", StringComparison.OrdinalIgnoreCase) ||
                                         (url.Split('/').Length > 6 && url.EndsWith(".html", StringComparison.OrdinalIgnoreCase))))
                            .Distinct()
                            .ToList();

                        _logger.LogInformation("Found {Count} article(s) in {Section}", links.Count, sectionTitle);
                        articleUrls.AddRange(links);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error processing section");
                }
            }

            // Fallback: If no sections found, try a simpler approach
            if (articleUrls.Count == 0)
            {
                _logger.LogWarning("No matching sections found, using fallback article extraction");

                // Get all article links on the page
                var allLinks = driver.FindElements(By.CssSelector($"a[href*='/{currentYear}/'], a[href*='/{previousYear}/']"))
                    .Select(e => e.GetAttribute("href"))
                    .Where(url => !string.IsNullOrWhiteSpace(url) &&
                                (url.Contains("/article/", StringComparison.OrdinalIgnoreCase) ||
                                 (url.Split('/').Length > 6 && url.EndsWith(".html", StringComparison.OrdinalIgnoreCase))))
                    .Distinct()
                    .ToList();

                // Take only from the beginning of the page
                articleUrls = allLinks.Take(maxArticles).ToList();
            }

            var finalUrls = articleUrls.Distinct().ToList();
            _logger.LogInformation("Total articles selected: {Count}", finalUrls.Count);

            return finalUrls;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting article URLs");
            return new List<string>();
        }
    }

    private async Task<(bool Success, IWebDriver? NewDriver)> PromptForManualChallengeCompletionAsync(
        IWebDriver headlessDriver,
        string url,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          Challenge Page Detected - Manual Completion Required          ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("NYT is blocking the headless browser. A visible browser window will open");
        Console.WriteLine("for you to complete any challenges and continue scraping.");
        Console.WriteLine();
        Console.Write("Open visible browser to continue? [Y/n]: ");

        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response == "n" || response == "no")
        {
            Console.WriteLine("Skipping - will retry with headless browser...");
            Console.WriteLine();
            return (false, null);
        }

        Console.WriteLine();
        Console.WriteLine("Opening visible browser...");
        Console.WriteLine();

        try
        {
            // Create a headed Chrome browser for manual interaction
            var headedDriver = CreateHeadedChromeDriver();

            // First, copy cookies from headless driver to maintain session
            var cookies = headlessDriver.Manage().Cookies.AllCookies;

            // Navigate to NYT first to set the domain
#pragma warning disable S6966
            headedDriver.Navigate().GoToUrl("https://www.nytimes.com");
#pragma warning restore S6966
            await Task.Delay(2000, cancellationToken);

            // Add cookies from the headless session
            foreach (var cookie in cookies)
            {
                try
                {
                    headedDriver.Manage().Cookies.AddCookie(cookie);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not copy cookie {Name}", cookie.Name);
                }
            }

            // Navigate to the target URL
#pragma warning disable S6966
            headedDriver.Navigate().GoToUrl(url);
#pragma warning restore S6966

            Console.WriteLine("────────────────────────────────────────────────────────────────────────");
            Console.WriteLine();
            Console.WriteLine("A Chrome browser window has opened.");
            Console.WriteLine("If there's a CAPTCHA, please complete it now.");
            Console.WriteLine();
            Console.WriteLine("Press ENTER to continue scraping with the visible browser...");
            Console.WriteLine("(The browser will remain open until scraping completes)");
            Console.WriteLine();

            // Wait for user to press Enter
            Console.ReadLine();

            Console.WriteLine("✓ Continuing with visible browser...");
            Console.WriteLine();

            _logger.LogInformation("Switching to headed browser for scraping");

            // Dispose the headless driver and return the headed one
            headlessDriver.Dispose();

            return (true, headedDriver);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during manual challenge completion");
            Console.WriteLine($"✗ Error: {ex.Message}");
            Console.WriteLine("Continuing with automatic retry...");
            Console.WriteLine();
            return (false, null);
        }
    }

    private IWebDriver CreateHeadedChromeDriver()
    {
        var options = new ChromeOptions();

        // Anti-detection measures
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);
        options.AddArgument($"user-agent={_browserConfig.UserAgent}");

        // Headed mode with visible window
        options.AddArgument("--window-size=1200,800");
        options.AddArgument("--window-position=100,100");

        // Create ChromeDriver service
        var service = ChromeDriverService.CreateDefaultService();
        service.SuppressInitialDiagnosticInformation = true;
        service.HideCommandPromptWindow = true;

        var driver = new ChromeDriver(service, options);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60); // Longer timeout for manual interaction

        // Execute script to mask WebDriver detection
        var script = @"
            Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
            window.navigator.chrome = {runtime: {}};
        ";
        ((IJavaScriptExecutor)driver).ExecuteScript(script);

        _logger.LogDebug("Headed Chrome driver created for manual challenge completion");
        return driver;
    }
}
