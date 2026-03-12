// Educational and personal use only.

using System.Diagnostics;
using System.Net;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Loads web pages using Selenium WebDriver.
/// Wraps browser automation for the terminal browser mode.
/// </summary>
public class PageLoader : IPageLoader
{
    private readonly BrowserConfiguration _browserConfig;
    private readonly ILogger<PageLoader> _logger;
    private readonly HttpClient? _httpClient;
    private readonly IBrowserSession _browserSession;

    public PageLoader(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<PageLoader> logger,
        IBrowserSession browserSession,
        HttpClient? httpClient = null)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
        _browserSession = browserSession;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Checks if the loaded HTML is a bot challenge or CAPTCHA page rather than real content.
    /// Detects DataDome, Cloudflare challenge, and generic CAPTCHA interstitials.
    /// </summary>
    public static bool IsBotChallengePage(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return false;
        }

        // DataDome detection
        if (html.Contains("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("datadome", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Cloudflare challenge detection
        if (html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Generic CAPTCHA detection: very small page with captcha/challenge keywords
        const int smallPageThreshold = 5 * 1024;
        if (html.Length < smallPageThreshold &&
            (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public async Task<PageLoadResult> LoadAsync(PageLoadRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading page: {Url}", request.Url);
        var totalSw = Stopwatch.StartNew();

        try
        {
            // PreferSelenium: try Selenium first, fall back to HTTP
            if (request.PreferSelenium && !request.ForceBrowser)
            {
                return await LoadSeleniumFirstAsync(request, totalSw, cancellationToken);
            }

            // Default path: try HTTP first, fall back to Selenium
            // Skip HTTP when ForceBrowser is set (e.g., paywalled domains with cookies)
            if (request.ForceBrowser)
            {
                _logger.LogInformation("ForceBrowser set, skipping HTTP fetch for {Url}", request.Url);
            }
            else if (_httpClient != null)
            {
                _logger.LogInformation("HttpClient available, attempting HTTP fetch for {Url}", request.Url);
                var httpSw = Stopwatch.StartNew();
                var httpResult = await TryHttpFetchAsync(request, cancellationToken);
                httpSw.Stop();

                if (httpResult.Success)
                {
                    totalSw.Stop();
                    _logger.LogDebug("Page loaded in {ElapsedMs}ms via HTTP: {Url}", totalSw.ElapsedMilliseconds, request.Url);
                    return httpResult;
                }

                _logger.LogInformation(
                    "HTTP fetch failed in {ElapsedMs}ms ({Error}), falling back to browser",
                    httpSw.ElapsedMilliseconds,
                    httpResult.ErrorMessage);
            }
            else
            {
                _logger.LogInformation("No HttpClient injected, going directly to browser for {Url}", request.Url);
            }

            // Fall back to browser for JavaScript-heavy sites
            var browserSw = Stopwatch.StartNew();
            var result = await BrowserFetchAsync(request, cancellationToken);
            browserSw.Stop();
            totalSw.Stop();
            _logger.LogDebug(
                "Page loaded in {ElapsedMs}ms via browser (browser: {BrowserMs}ms): {Url}",
                totalSw.ElapsedMilliseconds,
                browserSw.ElapsedMilliseconds,
                request.Url);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private static bool IsJavaScriptRequired(string html)
    {
        var lowerHtml = html.ToLowerInvariant();

        // Check for Cloudflare/DataDome challenge/block pages - these need browser to solve
        var cloudflareIndicators = new[]
        {
            "attention required! | cloudflare",
            "you have been blocked",
            "checking your browser",
            "cf-browser-verification",
            "cf-challenge",
            "challenge-platform",
            "just a moment...",
            "enable cookies",
            "captcha-delivery.com",
            "datadome",
            "geo.captcha-delivery.com"
        };

        if (Array.Exists(cloudflareIndicators, i => lowerHtml.Contains(i)))
        {
            return true;
        }

        // Check for explicit "enable/require JavaScript" messages
        var indicators = new[]
        {
            "please enable javascript",
            "please enable js",
            "javascript is required",
            "this page requires javascript",
            "you need to enable javascript",
        };

        if (Array.Exists(indicators, i => lowerHtml.Contains(i)))
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
        var author = GetNonUrlAuthor(doc);
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
        // ExtractMetaContent already decodes HTML entities
        var ogTitle = ExtractMetaContent(doc, "og:title");
        if (!string.IsNullOrWhiteSpace(ogTitle))
        {
            return ogTitle;
        }

        // Fall back to <title> tag
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        var title = titleNode?.InnerText.Trim();
        return title != null ? WebUtility.HtmlDecode(title) : null;
    }

    private static string? ExtractMetaContent(HtmlDocument doc, string name)
    {
        return HtmlMetadataExtractor.ExtractMetaContent(doc, name);
    }

    private static string? GetNonUrlAuthor(HtmlDocument doc)
    {
        var metaAuthor = ExtractMetaContent(doc, "author");
        if (!string.IsNullOrWhiteSpace(metaAuthor) && !IsAuthorUrl(metaAuthor))
        {
            return metaAuthor;
        }

        var articleAuthor = ExtractMetaContent(doc, "article:author");
        if (!string.IsNullOrWhiteSpace(articleAuthor) && !IsAuthorUrl(articleAuthor))
        {
            return articleAuthor;
        }

        return null;
    }

    private static bool IsAuthorUrl(string text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? ExtractCanonicalUrl(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']");
        return node?.GetAttributeValue("href", null!);
    }

    private static DateTime? ParsePublishedDate(HtmlDocument doc)
    {
        return HtmlMetadataExtractor.ExtractPublishedDate(doc);
    }

    private static string? ExtractFaviconUrl(HtmlDocument doc, string pageUrl)
    {
        var node = doc.DocumentNode.SelectSingleNode("//link[@rel='icon']") ??
                   doc.DocumentNode.SelectSingleNode("//link[@rel='shortcut icon']");

        var href = node?.GetAttributeValue("href", null!);

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

    private async Task<PageLoadResult> LoadSeleniumFirstAsync(
        PageLoadRequest request,
        Stopwatch totalSw,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("PreferSelenium set, attempting browser fetch first for {Url}", request.Url);
        var browserSw = Stopwatch.StartNew();
        var browserResult = await BrowserFetchAsync(request, cancellationToken);
        browserSw.Stop();

        if (browserResult.Success)
        {
            totalSw.Stop();
            _logger.LogDebug(
                "Page loaded in {ElapsedMs}ms via browser (PreferSelenium): {Url}",
                totalSw.ElapsedMilliseconds,
                request.Url);
            return browserResult;
        }

        _logger.LogInformation(
            "Browser fetch failed in {ElapsedMs}ms ({Error}), falling back to HTTP",
            browserSw.ElapsedMilliseconds,
            browserResult.ErrorMessage);

        if (_httpClient != null)
        {
            var httpSw = Stopwatch.StartNew();
            var httpResult = await TryHttpFetchAsync(request, cancellationToken);
            httpSw.Stop();
            totalSw.Stop();

            if (httpResult.Success)
            {
                _logger.LogDebug(
                    "Page loaded in {ElapsedMs}ms via HTTP fallback (PreferSelenium): {Url}",
                    totalSw.ElapsedMilliseconds,
                    request.Url);
                return httpResult;
            }

            _logger.LogInformation(
                "HTTP fallback also failed in {ElapsedMs}ms ({Error})",
                httpSw.ElapsedMilliseconds,
                httpResult.ErrorMessage);
            return httpResult;
        }

        _logger.LogInformation("No HttpClient available for fallback after browser failure");
        totalSw.Stop();
        return browserResult;
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
            cts.CancelAfter(_browserConfig.HttpTimeoutMs);

            using var response = await _httpClient.SendAsync(httpRequest, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return PageLoadResult.Failure($"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);

            // Check if this looks like a JavaScript-required page
            var jsRequired = IsJavaScriptRequired(html);
            _logger.LogInformation("HTTP response: {Length} bytes, JS required: {JsRequired}", html.Length, jsRequired);

            if (jsRequired)
            {
                _logger.LogDebug("Page requires JavaScript, will use browser fallback");
                return PageLoadResult.Failure("JavaScript required");
            }

            // Check for JS shell pages with article markup but no actual content
            if (ReadableContentExtractor.IsEmptyArticleShell(html))
            {
                _logger.LogDebug("HTTP response is an empty article shell, will use browser fallback");
                return PageLoadResult.Failure("Empty article shell");
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
        try
        {
            var driver = _browserSession.GetOrCreateDriver(request.Headless);

            _logger.LogDebug("Navigating to {Url}", request.Url);
#pragma warning disable S6966 // Selenium WebDriver does not provide async navigation
            driver.Navigate().GoToUrl(request.Url);
#pragma warning restore S6966

            // Wait for page to load
            await WaitForPageLoadAsync(driver, request.TimeoutMs, cancellationToken);

            var finalUrl = driver.Url;
            var html = driver.PageSource;

            if (IsBotChallengePage(html))
            {
                _logger.LogWarning("Bot challenge page detected after browser load, polling for resolution: {Url}", request.Url);
                var resolved = await PollForChallengeResolutionAsync(driver, request.Url, cancellationToken);
                if (resolved != null)
                {
                    html = resolved;
                    finalUrl = driver.Url;
                    _logger.LogInformation("Bot challenge resolved after polling: {Url}", finalUrl);
                }
                else
                {
                    _logger.LogWarning("Bot challenge did not resolve within polling window: {Url}", request.Url);
                    return PageLoadResult.Failure("Bot challenge could not be resolved");
                }
            }

            var metadata = ExtractMetadata(html, finalUrl);

            _logger.LogInformation("Successfully loaded page via browser: {Url}", finalUrl);
            return PageLoadResult.Successful(finalUrl, html, metadata, FetchMethod.Selenium);
        }
        catch (WebDriverException ex)
        {
            _logger.LogError(ex, "Browser error loading page: {Url}", request.Url);
            return PageLoadResult.Failure($"Browser error: {ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogWarning(ex, "Browser session disposed during page load: {Url}", request.Url);
            return PageLoadResult.Failure("Browser session is no longer available");
        }
    }

    private async Task<string?> PollForChallengeResolutionAsync(
        IWebDriver driver,
        string url,
        CancellationToken cancellationToken)
    {
        var pollIntervalMs = _browserConfig.BotChallengePollIntervalMs;
        var maxWaitMs = _browserConfig.BotChallengeMaxWaitMs;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(pollIntervalMs, cancellationToken);

            try
            {
                var currentHtml = driver.PageSource;
                if (!IsBotChallengePage(currentHtml))
                {
                    return currentHtml;
                }

                _logger.LogDebug(
                    "Bot challenge still present after {ElapsedMs}ms, continuing to poll: {Url}",
                    sw.ElapsedMilliseconds,
                    url);
            }
            catch (WebDriverException ex)
            {
                _logger.LogWarning(ex, "Error polling page source during challenge resolution: {Url}", url);
                return null;
            }
        }

        return null;
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

            // Secondary wait: give JS time to render content into the DOM
            try
            {
                var contentWait = new WebDriverWait(driver, TimeSpan.FromSeconds(3));
                contentWait.Until(d =>
                {
                    var bodyLength = jsExecutor.ExecuteScript(
                        "return document.body ? document.body.innerHTML.length : 0")?.ToString();
                    return int.TryParse(bodyLength, out var len) && len > 1000;
                });
            }
            catch (WebDriverTimeoutException)
            {
                _logger.LogDebug("Content did not reach expected size within secondary wait");
            }

            // Optional post-load delay for sites needing extra JS rendering time
            if (_browserConfig.PostLoadDelayMs > 0)
            {
                await Task.Delay(_browserConfig.PostLoadDelayMs, cancellationToken);
            }
        }
        catch (WebDriverTimeoutException)
        {
            _logger.LogWarning("Timeout waiting for page load, continuing anyway");
        }
    }
}
