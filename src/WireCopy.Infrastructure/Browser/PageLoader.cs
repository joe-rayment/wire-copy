// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using System.Net;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Loads web pages using Playwright browser automation.
/// Wraps browser automation for the terminal browser mode.
/// </summary>
public class PageLoader : IPageLoader
{
    private readonly BrowserConfiguration _browserConfig;
    private readonly ILogger<PageLoader> _logger;
    private readonly HttpClient? _httpClient;
    private readonly IBrowserSession _browserSession;
    private readonly IPageAccessQueue? _pageAccessQueue;

    public PageLoader(
        IOptions<BrowserConfiguration> browserConfig,
        ILogger<PageLoader> logger,
        IBrowserSession browserSession,
        HttpClient? httpClient = null,
        IPageAccessQueue? pageAccessQueue = null)
    {
        _browserConfig = browserConfig.Value;
        _logger = logger;
        _browserSession = browserSession;
        _httpClient = httpClient;

        // workspace-u4o9: when provided, every browser fetch runs under a Foreground
        // lease so it can never interleave with another navigator of the shared fetch
        // page (interactive-refresh, human-action watcher reloads). Null keeps the
        // legacy direct path for tests and standalone use.
        _pageAccessQueue = pageAccessQueue;
    }

    /// <summary>
    /// Checks if the loaded HTML is a bot challenge or CAPTCHA page rather than real content.
    /// Thin wrapper that delegates to <see cref="HumanActionDetector.IsBotChallenge"/> for
    /// backwards compatibility; new code should call <see cref="HumanActionDetector.Detect"/>
    /// directly to obtain a typed <see cref="Application.DTOs.Browser.HumanActionRequired"/> verdict.
    /// </summary>
    public static bool IsBotChallengePage(string html)
        => HumanActionDetector.IsBotChallenge(html);

    public async Task<PageLoadResult> LoadAsync(PageLoadRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading page: {Url}", request.Url);
        var totalSw = Stopwatch.StartNew();

        try
        {
            // PreferBrowser: try browser first, fall back to HTTP
            if (request.PreferBrowser && !request.ForceBrowser)
            {
                return await LoadBrowserFirstAsync(request, totalSw, cancellationToken).ConfigureAwait(false);
            }

            // Default path: try HTTP first, fall back to browser
            // Skip HTTP when ForceBrowser is set (e.g., paywalled domains with cookies)
            if (request.ForceBrowser)
            {
                _logger.LogInformation("ForceBrowser set, skipping HTTP fetch for {Url}", request.Url);
            }
            else if (_httpClient != null)
            {
                _logger.LogInformation("HttpClient available, attempting HTTP fetch for {Url}", request.Url);
                var httpSw = Stopwatch.StartNew();
                var httpResult = await TryHttpFetchAsync(request, cancellationToken).ConfigureAwait(false);
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
            var result = await BrowserFetchAsync(request, cancellationToken).ConfigureAwait(false);
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
        var result = await LoadAsync(new PageLoadRequest { Url = url }, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to load page: {result.ErrorMessage}");
        }

        return result.Html;
    }

    /// <summary>
    /// Executes JavaScript to dismiss login, subscription, and cookie overlays
    /// that cover page content. Also restores scrolling on the body element.
    /// Called after page load and after bot challenge resolution.
    /// </summary>
    internal static async Task DismissOverlaysAsync(IPage page, ILogger logger)
    {
        try
        {
            var removedCount = await page.EvaluateAsync<int?>(@"() => {
                var removed = 0;

                // Selectors targeting paywall, login, and subscription overlays
                var selectors = [
                    '[class*=""gateway""]',
                    '[class*=""regwall""]',
                    '[class*=""paywall""]',
                    '[class*=""subscriber-gate""]',
                    '[class*=""subscribe-wall""]',
                    '[class*=""expanded-dock""]',
                    '[data-testid*=""paywall""]',
                    '[data-testid*=""inline-message""]',
                    '[data-testid*=""gateway""]',
                    '[class*=""css-mcm29f""]'
                ];

                // Skip removal if element contains substantial text (likely article content)
                function hasSubstantialText(el) {
                    var text = (el.innerText || '').trim();
                    return text.length > 200;
                }

                // Remove matching elements (skip if they contain <article> or substantial text
                // to avoid removing content wrappers like NYT's vi-gateway-container)
                selectors.forEach(function(sel) {
                    document.querySelectorAll(sel).forEach(function(el) {
                        if (el.querySelector('article') || hasSubstantialText(el)) return;
                        // Only remove elements that look like overlays (fixed/sticky position
                        // or high z-index)
                        var style = window.getComputedStyle(el);
                        var isOverlay = style.position === 'fixed' || style.position === 'sticky'
                            || style.zIndex > 100;
                        if (isOverlay) {
                            el.remove();
                            removed++;
                        }
                    });
                });

                // Remove generic fixed/sticky overlays covering viewport
                document.querySelectorAll('[class*=""modal""], [class*=""overlay""], [class*=""popup""], [class*=""curtain""], [class*=""backdrop""]').forEach(function(el) {
                    if (hasSubstantialText(el)) return;
                    var style = window.getComputedStyle(el);
                    if ((style.position === 'fixed' || style.position === 'sticky') && style.display !== 'none') {
                        el.remove();
                        removed++;
                    }
                });

                // Restore scrolling on body and html
                document.documentElement.style.overflow = '';
                document.body.style.overflow = '';
                document.documentElement.style.overflowY = '';
                document.body.style.overflowY = '';
                document.documentElement.classList.remove('noscroll', 'no-scroll', 'modal-open', 'overlay-open');
                document.body.classList.remove('noscroll', 'no-scroll', 'modal-open', 'overlay-open');

                return removed;
            }").ConfigureAwait(false);

            if (removedCount.HasValue && removedCount.Value > 0)
            {
                logger.LogInformation("Dismissed {Count} overlay element(s) from page", removedCount.Value);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to dismiss overlays (non-fatal)");
        }
    }

    private static bool IsJavaScriptRequired(string html)
    {
        // Cloudflare / DataDome / general bot-challenge pages — these need a browser to solve.
        // Delegated to the consolidated HumanActionDetector to keep the bot-string list in one place.
        if (HumanActionDetector.IsBotDetectionResponse(html))
        {
            return true;
        }

        // Explicit "enable/require JavaScript" messages — distinct from bot detection.
        var indicators = new[]
        {
            "please enable javascript",
            "please enable js",
            "javascript is required",
            "this page requires javascript",
            "you need to enable javascript",
        };

        if (Array.Exists(indicators, i => html.Contains(i, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string ExtractDomainSafe(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
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
            Title = TextNormalizer.NormalizeDisplayText(title ?? "Untitled"),
            Description = description is null ? null : TextNormalizer.NormalizeDisplayText(description),
            CanonicalUrl = canonicalUrl,
            Author = author is null ? null : TextNormalizer.NormalizeDisplayText(author),
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

        // Fall back to <title> tag — NormalizeDisplayText at the call site
        // strips U+00A0 ("&nbsp;") and re-decodes any double-encoded entities.
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

    private async Task<PageLoadResult> LoadBrowserFirstAsync(
        PageLoadRequest request,
        Stopwatch totalSw,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("PreferBrowser set, attempting browser fetch first for {Url}", request.Url);
        var browserSw = Stopwatch.StartNew();
        var browserResult = await BrowserFetchAsync(request, cancellationToken).ConfigureAwait(false);
        browserSw.Stop();

        if (browserResult.Success)
        {
            totalSw.Stop();
            _logger.LogDebug(
                "Page loaded in {ElapsedMs}ms via browser (PreferBrowser): {Url}",
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
            var httpResult = await TryHttpFetchAsync(request, cancellationToken).ConfigureAwait(false);
            httpSw.Stop();
            totalSw.Stop();

            if (httpResult.Success)
            {
                _logger.LogDebug(
                    "Page loaded in {ElapsedMs}ms via HTTP fallback (PreferBrowser): {Url}",
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

            using var response = await _httpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var requestUrl = response.RequestMessage?.RequestUri?.ToString() ?? request.Url;

                // For auth/region status codes, surface a typed HITL signal so consumers
                // can render variant-aware copy instead of "HTTP 403".
                var bodyForDetect = string.Empty;
                try
                {
                    bodyForDetect = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Body read failures are non-fatal here — we still emit the failure with status code.
                }

                var action = HumanActionDetector.Detect(bodyForDetect, requestUrl, status);
                if (action != null)
                {
                    return PageLoadResult.Failure(action, $"HTTP {status}", status);
                }

                return PageLoadResult.Failure($"HTTP {status}", status);
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

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

            // For pages that look like articles, verify they have extractable content.
            // JS-heavy sites may return a shell with navigation/header text (passing
            // word-count checks) but no real article body that can be rendered.
            if (ReadableContentExtractor.IsArticlePage(html) &&
                !ReadableContentExtractor.HasExtractableContent(html))
            {
                _logger.LogDebug("HTTP response looks like an article but has no extractable content, will use browser fallback");
                return PageLoadResult.Failure("No extractable content");
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
        // workspace-m7nc: a Playwright IPage reference can become invalid mid-load when
        // the headed Chrome window navigates underneath us (most commonly: user just
        // solved a captcha). The page's underlying CDP target disposes, and the next
        // call surfaces as "Target page, context or browser has been closed". Retry
        // once with a fresh page from the session pool before surfacing the error —
        // the user shouldn't have to press Shift+R themselves.
        //
        // The retry MUST go through InvalidatePageAsync first; the BrowserSession
        // liveness check (`_ = _page.Url`) doesn't catch all stale-target shapes,
        // so without explicit invalidation the second attempt could see the same
        // dead IPage and fail identically.
        var first = await BrowserFetchOnceAsync(request, cancellationToken).ConfigureAwait(false);
        if (first.Success || !LooksLikeStalePlaywrightFailure(first))
        {
            return first;
        }

        _logger.LogWarning(
            "Playwright stale-page exception for {Url}; invalidating page and retrying once",
            request.Url);
        try
        {
            await _browserSession.InvalidatePageAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InvalidatePageAsync threw before retry for {Url}", request.Url);
        }

        var second = await BrowserFetchOnceAsync(request, cancellationToken).ConfigureAwait(false);
        if (second.Success || !LooksLikeStalePlaywrightFailure(second))
        {
            return second;
        }

        // Both attempts saw a stale target — surface user-actionable copy instead
        // of the opaque Playwright "Target page, context or browser has been closed".
        return PageLoadResult.Failure(
            "Page navigated mid-load (e.g. after a captcha solve). Press Shift+R to retry.");
    }

#pragma warning disable SA1204 // helper kept adjacent to its sole caller for readability (workspace-m7nc).
    private static bool LooksLikeStalePlaywrightFailure(PageLoadResult result)
    {
        return !result.Success
            && result.ErrorMessage is { } msg
            && LooksLikeStalePlaywrightPage(msg);
    }
#pragma warning restore SA1204

#pragma warning disable SA1202, SA1204 // helper kept adjacent to its sole caller for readability (workspace-m7nc).
    /// <summary>
    /// Heuristic: did this Playwright failure look like an in-flight page that got
    /// invalidated by an out-of-band navigation (typical captcha-solve scenario)?
    /// Used by <see cref="BrowserFetchAsync"/> to retry once with a fresh page
    /// (workspace-m7nc).
    /// </summary>
    internal static bool LooksLikeStalePlaywrightPage(string errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("Target page", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("context or browser has been closed", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("Target closed", StringComparison.OrdinalIgnoreCase);
    }
#pragma warning restore SA1202, SA1204

#pragma warning disable SA1202, SA1204 // helper kept adjacent to its sole caller for readability (workspace-odn5).
    /// <summary>
    /// Heuristic: did this Playwright failure look like a redirect loop? Chromium
    /// surfaces an unresolvable redirect cycle as
    /// <c>net::ERR_TOO_MANY_REDIRECTS at &lt;url&gt;</c> (confirmed live against a
    /// 302-looping origin and intermittently on Cloudflare-fronted macleans.ca).
    /// Used by <see cref="BrowserFetchOnceAsync"/> to surface a typed
    /// <see cref="HumanActionVariant.RedirectLoop"/> verdict instead of a raw
    /// "Browser error: net::ERR_TOO_MANY_REDIRECTS" string (workspace-odn5).
    /// </summary>
    internal static bool LooksLikeRedirectLoop(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        // Match both the Chromium net-error token and the humanised phrasing that
        // some wrappers emit for the same redirect-cycle condition.
        return errorMessage.Contains("ERR_TOO_MANY_REDIRECTS", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("too many redirects", StringComparison.OrdinalIgnoreCase);
    }
#pragma warning restore SA1202, SA1204

#pragma warning disable SA1202, SA1204 // helper kept adjacent to its sole caller for readability (workspace-odn5).
    /// <summary>
    /// Heuristic: did this Playwright failure look like a navigation that got
    /// bounced into the browser's own error page? Chromium reports this as
    /// <c>Navigation to "X" is interrupted by another navigation to
    /// "chrome-error://chromewebdata/"</c> — observed live as a sibling of the
    /// redirect-cycle on Cloudflare-fronted macleans.ca (the consent/bot bounce
    /// supersedes the real navigation and lands on an error page). Requires BOTH
    /// markers so it never fires on a healthy client-side redirect (those
    /// interrupt to a real URL and succeed, never reaching this catch). Surfaced
    /// as the deliberately non-committal <see cref="HumanActionVariant.Generic"/>
    /// verdict — the root cause is genuinely ambiguous, and the workspace-0b9s
    /// rule is to prefer generic actionable copy over a guessed specific cause.
    /// </summary>
    internal static bool LooksLikeInterruptedNavigation(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("interrupted by another navigation", StringComparison.OrdinalIgnoreCase)
            && errorMessage.Contains("chrome-error", StringComparison.OrdinalIgnoreCase);
    }
#pragma warning restore SA1202, SA1204

    private async Task<PageLoadResult> BrowserFetchOnceAsync(PageLoadRequest request, CancellationToken cancellationToken)
    {
        if (!_browserSession.IsBrowserAvailable)
        {
            _logger.LogDebug("Browser unavailable on this platform, skipping browser fetch for {Url}", request.Url);
            return PageLoadResult.Failure("Browser unavailable on this platform");
        }

        try
        {
            IPage page;
            PageLease? lease = null;
            try
            {
                using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pageCts.CancelAfter(TimeSpan.FromSeconds(30));

                // workspace-u4o9: hold the Foreground lease for the WHOLE fetch
                // (navigation through content read) so nothing else can navigate the
                // fetch page out from under us mid-load.
                if (_pageAccessQueue != null)
                {
                    lease = await _pageAccessQueue
                        .AcquireAsync(PageAccessPriority.Foreground, request.Headless, pageCts.Token)
                        .ConfigureAwait(false);
                    page = lease.Page;
                }
                else
                {
                    page = await _browserSession.GetOrCreatePageAsync(request.Headless).WaitAsync(pageCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Browser session creation timed out after 30s for {Url}", request.Url);
                return PageLoadResult.Failure("Browser launch timed out");
            }

            using var heldLease = lease;

            _logger.LogDebug("Navigating to {Url}", request.Url);
            await page.GotoAsync(request.Url, new PageGotoOptions { Timeout = request.TimeoutMs }).ConfigureAwait(false);

            // Wait for page to load
            await WaitForPageLoadAsync(page, request.TimeoutMs, cancellationToken).ConfigureAwait(false);

            var pageContent = await page.ContentAsync().ConfigureAwait(false);
            if (IsBotChallengePage(pageContent))
            {
                // Speed fix (workspace-0b9s QA #5): emit the typed HITL verdict
                // immediately on first detection instead of paying the 60s
                // PollForChallengeResolutionAsync window. The reader-view box
                // appears in <1s with verb-led copy ("Site is showing a CAPTCHA",
                // "Solve it in the browser, then press R"), and the user can
                // Shift+O to open in their real browser, solve, then press R
                // to retry. The 60s blind wait gave the user nothing actionable
                // to do for a full minute and almost never resolved on its own
                // in headless mode anyway.
                _logger.LogWarning(
                    "Bot challenge page detected after browser load, surfacing verdict (no poll wait): {Url}",
                    request.Url);
                var action = HumanActionDetector.Detect(pageContent, page.Url, statusCode: 0)
                    ?? new HumanActionRequired(HumanActionVariant.Captcha, ExtractDomainSafe(page.Url));
                return PageLoadResult.Failure(action, "Bot challenge detected");
            }

            // Dismiss login/subscription overlays that may cover content
            await DismissOverlaysAsync(page, _logger).ConfigureAwait(false);
            var finalUrl = page.Url;
            var html = await page.ContentAsync().ConfigureAwait(false);

            // Even after overlay dismissal, surface a typed HITL signal if the post-load
            // markup still looks like a CAPTCHA / login wall / cookie banner / region block.
            // This is a non-fatal annotation: success path stays unchanged when the detector
            // returns null, but the caller can still inspect RequiredAction on success when
            // we do detect a soft block (e.g., a paywall preview that "loaded" but is gated).
            var detectedAction = HumanActionDetector.Detect(html, finalUrl, statusCode: 0);

            var metadata = ExtractMetadata(html, finalUrl);

            _logger.LogInformation("Successfully loaded page via browser: {Url}", finalUrl);
            return PageLoadResult.Successful(finalUrl, html, metadata, FetchMethod.Browser) with { RequiredAction = detectedAction };
        }
        catch (PlaywrightException ex)
        {
            _logger.LogError(ex, "Browser error loading page: {Url}", request.Url);

            // workspace-odn5: a redirect cycle (net::ERR_TOO_MANY_REDIRECTS) is a
            // typed, actionable condition — almost always a cookie/consent or
            // Cloudflare bot bounce that won't settle in our session. Surface a
            // RedirectLoop verdict so the orchestrator renders the "open in your
            // browser, then press R" box instead of a raw Playwright error string.
            // Checked before the stale-page heuristic (a loop is not a stale page,
            // so this never steals the workspace-m7nc retry path).
            if (LooksLikeRedirectLoop(ex.Message))
            {
                var domain = ExtractDomainSafe(request.Url);
                _logger.LogWarning("Redirect loop loading {Url} via browser, surfacing RedirectLoop verdict", request.Url);
                return PageLoadResult.Failure(
                    new HumanActionRequired(HumanActionVariant.RedirectLoop, domain, "redirect loop"),
                    "Redirect loop");
            }

            // workspace-odn5: the same Cloudflare bounce sometimes supersedes the
            // navigation and lands on chrome-error:// instead of looping. Surface a
            // Generic ("uncertain interruption") verdict so the user still gets an
            // actionable box rather than a raw multi-line Playwright error string.
            if (LooksLikeInterruptedNavigation(ex.Message))
            {
                var domain = ExtractDomainSafe(request.Url);
                _logger.LogWarning("Navigation interrupted into an error page loading {Url} via browser, surfacing Generic verdict", request.Url);
                return PageLoadResult.Failure(
                    new HumanActionRequired(HumanActionVariant.Generic, domain, "navigation interrupted"),
                    "Navigation interrupted");
            }

            // workspace-m7nc: stale-page detection happens in the outer
            // BrowserFetchAsync — preserve the raw Playwright message here so
            // LooksLikeStalePlaywrightFailure can route through the retry path.
            // The friendlier user-facing copy is applied AFTER the retry also fails.
            if (LooksLikeStalePlaywrightPage(ex.Message))
            {
                return PageLoadResult.Failure(ex.Message);
            }

            return PageLoadResult.Failure($"Browser error: {ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogWarning(ex, "Browser session disposed during page load: {Url}", request.Url);
            return PageLoadResult.Failure("Browser session is no longer available");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Browser unavailable for page load: {Url}", request.Url);
            return PageLoadResult.Failure("Browser unavailable");
        }
    }

    private async Task WaitForPageLoadAsync(IPage page, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            // Wait for DOM ready (not NetworkIdle — heavy sites like NYT make dozens of
            // tracking/ad requests that delay NetworkIdle by 30+ seconds while content is
            // already rendered). The secondary JS check below gives extra time for rendering.
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = timeoutMs,
            }).ConfigureAwait(false);

            // Secondary wait: give JS time to render content into the DOM.
            // First check for structural selectors (fast exit if found), then wait
            // for article paragraph text to appear (needed for React-rendered sites
            // like NYT where the DOM shell loads before content is hydrated).
            try
            {
                await page.WaitForFunctionAsync(
                    @"() => {
                        // Quick check: structural article elements
                        if (document.querySelector('[role=""main""] p, article p, .entry-content p, .post-content p')) return true;
                        // NYT-specific: StoryBodyCompanionColumn or storyContent testid
                        if (document.querySelector('[data-testid=""storyContent""] p, .StoryBodyCompanionColumn p')) return true;
                        // Fallback: enough raw content rendered
                        return document.body && document.body.innerHTML.length > 5000;
                    }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 5000 }).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Content did not reach expected size within secondary wait");
            }
            catch (PlaywrightException)
            {
                _logger.LogDebug("Content did not reach expected size within secondary wait");
            }

            // Optional post-load delay for sites needing extra JS rendering time
            if (_browserConfig.PostLoadDelayMs > 0)
            {
                await Task.Delay(_browserConfig.PostLoadDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout waiting for page load, continuing anyway");
        }
        catch (PlaywrightException)
        {
            _logger.LogWarning("Timeout waiting for page load, continuing anyway");
        }
    }
}
