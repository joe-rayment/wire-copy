// Educational and personal use only.

using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.DTOs.Browser;
using NYTAudioScraper.Application.Interfaces.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;
using NYTAudioScraper.Infrastructure.Configuration;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using OpenQA.Selenium.Support.UI;

namespace NYTAudioScraper.Infrastructure.Browser;

/// <summary>
/// Loads web pages using Selenium WebDriver.
/// Wraps browser automation for the terminal browser mode.
/// </summary>
public class PageLoader : IPageLoader
{
    private readonly BrowserConfiguration _browserConfig;
    private readonly ILogger<PageLoader> _logger;
    private readonly HttpClient? _httpClient;

    public PageLoader(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<PageLoader> logger,
        HttpClient? httpClient = null)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<PageLoadResult> LoadAsync(PageLoadRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading page: {Url}", request.Url);

        try
        {
            // First try simple HTTP fetch (faster, no browser overhead)
            if (_httpClient != null)
            {
                _logger.LogInformation("HttpClient available, attempting HTTP fetch for {Url}", request.Url);
                var httpResult = await TryHttpFetchAsync(request, cancellationToken);
                if (httpResult.Success)
                {
                    return httpResult;
                }

                _logger.LogInformation("HTTP fetch failed ({Error}), falling back to browser", httpResult.ErrorMessage);
            }
            else
            {
                _logger.LogInformation("No HttpClient injected, going directly to browser for {Url}", request.Url);
            }

            // Fall back to browser for JavaScript-heavy sites
            return await BrowserFetchAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PageLoadResult.Failure("Operation cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading page: {Url}", request.Url);
            return PageLoadResult.Failure(ex.Message);
        }
    }

    public async Task<string> GetPageSourceAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await LoadAsync(new PageLoadRequest { Url = url }, cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to load page: {result.ErrorMessage}");
        }

        return result.Html;
    }

    private async Task<PageLoadResult> TryHttpFetchAsync(PageLoadRequest request, CancellationToken cancellationToken)
    {
        if (_httpClient == null)
        {
            return PageLoadResult.Failure("HttpClient not available");
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
            httpRequest.Headers.Add("User-Agent", _browserConfig.UserAgent);
            httpRequest.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            httpRequest.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            httpRequest.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            httpRequest.Headers.Add("Connection", "keep-alive");
            httpRequest.Headers.Add("Upgrade-Insecure-Requests", "1");
            httpRequest.Headers.Add("Sec-Fetch-Dest", "document");
            httpRequest.Headers.Add("Sec-Fetch-Mode", "navigate");
            httpRequest.Headers.Add("Sec-Fetch-Site", "none");
            httpRequest.Headers.Add("Sec-Fetch-User", "?1");
            httpRequest.Headers.Add("Cache-Control", "max-age=0");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(request.TimeoutMs);

            var response = await _httpClient.SendAsync(httpRequest, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return PageLoadResult.Failure($"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);

            // Debug: Save HTML to file for analysis
            try
            {
                var debugPath = "/tmp/macleans_http_debug.html";
                await System.IO.File.WriteAllTextAsync(debugPath, html, cts.Token);
            }
            catch { /* ignore */ }

            // Check if this looks like a JavaScript-required page
            var jsRequired = IsJavaScriptRequired(html);
            _logger.LogInformation("HTTP response: {Length} bytes, JS required: {JsRequired}", html.Length, jsRequired);

            if (jsRequired)
            {
                _logger.LogDebug("Page requires JavaScript, will use browser fallback");
                return PageLoadResult.Failure("JavaScript required");
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? request.Url;
            var metadata = ExtractMetadata(html, finalUrl);

            _logger.LogInformation("Successfully loaded page via HTTP: {Url}", finalUrl);
            return PageLoadResult.Successful(finalUrl, html, metadata);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HTTP fetch failed for {Url}", request.Url);
            return PageLoadResult.Failure(ex.Message);
        }
    }

    private async Task<PageLoadResult> BrowserFetchAsync(PageLoadRequest request, CancellationToken cancellationToken)
    {
        IWebDriver? driver = null;

        try
        {
            driver = CreateWebDriver(request.Headless);

            _logger.LogDebug("Navigating to {Url}", request.Url);
#pragma warning disable S6966 // Selenium WebDriver does not provide async navigation
            driver.Navigate().GoToUrl(request.Url);
#pragma warning restore S6966

            // Wait for page to load
            await WaitForPageLoadAsync(driver, request.TimeoutMs, cancellationToken);

            var finalUrl = driver.Url;
            var html = driver.PageSource;

            // Debug: Save HTML to file for analysis
            try
            {
                var debugPath = "/tmp/macleans_debug.html";
                await System.IO.File.WriteAllTextAsync(debugPath, html, cancellationToken);
                _logger.LogDebug("Saved debug HTML to {Path} ({Length} bytes)", debugPath, html.Length);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to save debug HTML");
            }

            var metadata = ExtractMetadata(html, finalUrl);

            _logger.LogInformation("Successfully loaded page via browser: {Url}", finalUrl);
            return PageLoadResult.Successful(finalUrl, html, metadata);
        }
        catch (WebDriverException ex)
        {
            _logger.LogError(ex, "Browser error loading page: {Url}", request.Url);
            return PageLoadResult.Failure($"Browser error: {ex.Message}");
        }
        finally
        {
            driver?.Dispose();
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

    private async Task WaitForPageLoadAsync(IWebDriver driver, int timeoutMs, CancellationToken cancellationToken)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromMilliseconds(timeoutMs));
        var jsExecutor = (IJavaScriptExecutor)driver;

        try
        {
            // Wait for document.readyState to be complete
            wait.Until(d =>
            {
                var readyState = jsExecutor.ExecuteScript("return document.readyState")?.ToString();
                return readyState == "complete";
            });

            // Wait for dynamic content to load - many modern sites load content via JS
            await Task.Delay(3000, cancellationToken);

            // Scroll down to trigger lazy loading of content
            try
            {
                jsExecutor.ExecuteScript("window.scrollTo(0, document.body.scrollHeight / 2);");
                await Task.Delay(1000, cancellationToken);
                jsExecutor.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                await Task.Delay(1000, cancellationToken);
                jsExecutor.ExecuteScript("window.scrollTo(0, 0);");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during scroll for lazy loading");
            }

            // Additional wait for any pending network requests to complete
            try
            {
                wait.Until(d =>
                {
                    var pendingRequests = jsExecutor.ExecuteScript(
                        "return window.performance.getEntriesByType('resource').filter(r => r.responseEnd === 0).length")?.ToString();
                    return pendingRequests == "0" || pendingRequests == null;
                });
            }
            catch (WebDriverTimeoutException)
            {
                // Some requests may still be pending, continue anyway
                _logger.LogDebug("Some network requests still pending after timeout");
            }
        }
        catch (WebDriverTimeoutException)
        {
            _logger.LogWarning("Timeout waiting for page load, continuing anyway");
        }
    }

    private static bool IsJavaScriptRequired(string html)
    {
        var lowerHtml = html.ToLowerInvariant();

        // Check for Cloudflare challenge/block pages - these need browser to solve
        var cloudflareIndicators = new[]
        {
            "attention required! | cloudflare",
            "you have been blocked",
            "checking your browser",
            "cf-browser-verification",
            "cf-challenge",
            "challenge-platform",
            "just a moment...",
            "enable cookies"
        };

        if (Array.Exists(cloudflareIndicators, i => lowerHtml.Contains(i)))
        {
            return true;
        }

        // Check for common indicators of JS-required pages
        // NOTE: Don't check for <noscript> as many sites have it for analytics but work fine without JS
        var indicators = new[]
        {
            "please enable javascript",
            "javascript is required",
            "this page requires javascript",
            "you need to enable javascript",
            // "loading..." removed - too common false positive
        };

        if (Array.Exists(indicators, i => lowerHtml.Contains(i)))
        {
            return true;
        }

        // Check for SPA frameworks
        if (lowerHtml.Contains("react") || lowerHtml.Contains("angular") || lowerHtml.Contains("vue"))
        {
            // If page is short with SPA framework, likely needs JS
            if (html.Length < 10000)
            {
                return true;
            }
        }

        // Count anchor tags - very few links often means content is JS-loaded
        var anchorCount = System.Text.RegularExpressions.Regex.Matches(lowerHtml, @"<a\s").Count;
        if (anchorCount < 10 && html.Length > 5000)
        {
            // Large HTML with few links suggests JS-rendered content
            return true;
        }

        // Check for empty main content areas (common in SPAs)
        if (lowerHtml.Contains("<main") || lowerHtml.Contains("<article"))
        {
            // Look for main/article tags that are essentially empty
            var mainMatch = System.Text.RegularExpressions.Regex.Match(
                lowerHtml,
                @"<main[^>]*>(.*?)</main>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (mainMatch.Success && mainMatch.Groups[1].Value.Trim().Length < 500)
            {
                return true;
            }
        }

        // Check for data-loading or skeleton UI patterns
        if (lowerHtml.Contains("skeleton") ||
            lowerHtml.Contains("loading-placeholder") ||
            lowerHtml.Contains("data-loading"))
        {
            return true;
        }

        return false;
    }

    private static PageMetadata ExtractMetadata(string html, string url)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = ExtractTitle(doc);
        var description = ExtractMetaContent(doc, "description") ?? ExtractMetaContent(doc, "og:description");
        var canonicalUrl = ExtractCanonicalUrl(doc) ?? ExtractMetaContent(doc, "og:url");
        var author = ExtractMetaContent(doc, "author") ?? ExtractMetaContent(doc, "article:author");
        var publishedDate = ParsePublishedDate(doc);
        var favicon = ExtractFaviconUrl(doc, url);

        return new PageMetadata
        {
            Title = title ?? "Untitled",
            Description = description,
            CanonicalUrl = canonicalUrl,
            Author = author,
            PublishedDate = publishedDate,
            FaviconUrl = favicon
        };
    }

    private static string? ExtractTitle(HtmlDocument doc)
    {
        // Try og:title first (usually cleaner)
        var ogTitle = ExtractMetaContent(doc, "og:title");
        if (!string.IsNullOrWhiteSpace(ogTitle))
        {
            return ogTitle;
        }

        // Fall back to <title> tag
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        return titleNode?.InnerText.Trim();
    }

    private static string? ExtractMetaContent(HtmlDocument doc, string name)
    {
        // Try name attribute
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@name='{name}']") ??
                   doc.DocumentNode.SelectSingleNode($"//meta[@property='{name}']");

        return node?.GetAttributeValue("content", null);
    }

    private static string? ExtractCanonicalUrl(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']");
        return node?.GetAttributeValue("href", null);
    }

    private static DateTime? ParsePublishedDate(HtmlDocument doc)
    {
        var dateString = ExtractMetaContent(doc, "article:published_time") ??
                        ExtractMetaContent(doc, "datePublished") ??
                        ExtractMetaContent(doc, "date");

        if (DateTime.TryParse(dateString, out var date))
        {
            return date;
        }

        return null;
    }

    private static string? ExtractFaviconUrl(HtmlDocument doc, string pageUrl)
    {
        var node = doc.DocumentNode.SelectSingleNode("//link[@rel='icon']") ??
                   doc.DocumentNode.SelectSingleNode("//link[@rel='shortcut icon']");

        var href = node?.GetAttributeValue("href", null);

        if (string.IsNullOrWhiteSpace(href))
        {
            // Default to /favicon.ico
            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Host}/favicon.ico";
            }

            return null;
        }

        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(new Uri(pageUrl), href, out var resolvedUri))
        {
            // Resolve relative URL
            return resolvedUri.ToString();
        }

        return href;
    }
}
