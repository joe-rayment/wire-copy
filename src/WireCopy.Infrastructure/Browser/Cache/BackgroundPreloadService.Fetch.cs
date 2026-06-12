// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Concurrent;
using System.Net;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;

namespace WireCopy.Infrastructure.Browser.Cache;

/// <summary>
/// Fetch + cache-warming portion of <see cref="BackgroundPreloadService"/>:
/// the HTTP and browser preload paths plus the page/article cache helpers.
/// Split out of the main file purely for size; behaviour is unchanged (workspace-dn1g).
/// </summary>
internal sealed partial class BackgroundPreloadService
{
    private async Task PreloadUrlAsync(string url, CancellationToken cancellationToken)
    {
        // Paywalled domains: re-check cookie validity before each attempt (cookies may
        // expire mid-session) and enforce per-session limit
        if (IsPaywalledDomain(url))
        {
            await RefreshPaywalledCookieStateAsync().ConfigureAwait(false);
            if (!_hasPaywalledCookies || _paywalledPreloadCount >= _config.MaxPaywalledPreloads)
            {
                _logger.LogDebug(
                    "Skipping preload for paywalled domain (cookies={HasCookies}, count={Count}/{Max}): {Url}",
                    _hasPaywalledCookies,
                    _paywalledPreloadCount,
                    _config.MaxPaywalledPreloads,
                    url);

                // Mark domain as needing JS so these URLs are counted in NeedsBrowserCount
                // on the next queue rebuild, rather than vanishing from progress tracking
                var origin = UrlNormalizer.GetOrigin(url);
                if (origin != null)
                {
                    _needsJsDomains[origin] = true;
                }

                NotifyProgressChanged();
                return;
            }
        }

        var normalizedUrl = UrlNormalizer.Normalize(url);

        // In-flight deduplication
        var tcs = new TaskCompletionSource<PageLoadResult>();
        var existing = _inFlight.GetOrAdd(normalizedUrl, tcs.Task);
        if (existing != tcs.Task)
        {
            // Another fetch is already in progress for this URL
            _logger.LogDebug("Skipping duplicate pre-load for {Url}", url);
            return;
        }

        // workspace-7xw0: track elapsed time + final outcome so the detail panel
        // can show "✓ url (320ms)" entries in its recent-history pane.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = PreloadOutcome.Failed;
        string? outcomeReason = null;

        try
        {
            _currentlyFetchingUrl = url;
            Interlocked.Exchange(ref _currentlyFetchingStartedAtTicks, DateTime.UtcNow.Ticks);
            _currentStage = PreloadStage.Fetching;
            _logger.LogDebug("Pre-loading: {Url}", url);
            var result = await HttpFetchAsync(url, cancellationToken).ConfigureAwait(false);

            _currentStage = PreloadStage.Detecting;

            if (result.Success)
            {
                // Typed HITL detection (workspace-0b9s): consolidate captcha / login /
                // cookie-consent / 2FA / paywall / region-block detection in one place
                // and surface the variant up through PreloadProgress so the status bar
                // shows "⏸ {verb} at {domain}" before the user even Enters the article.
                var hitlAction = HumanActionDetector.Detect(result.Html, result.Url ?? url, result.StatusCode);
                if (hitlAction != null || IsBotDetectionResponse(result.Html))
                {
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _circuitBrokenDomains[origin] = DateTime.UtcNow;
                        _logger.LogWarning(
                            "Human action required for {Origin} ({Variant}), stopping pre-loads for this domain",
                            origin,
                            hitlAction?.Variant.ToString() ?? "BotDetection");
                    }

                    // Surface the typed verdict to the UI; preserve the silent
                    // circuit-break behaviour (we still don't keep retrying).
                    if (hitlAction != null)
                    {
                        _lastBlockedAction = hitlAction;
                        NotifyProgressChanged();
                    }

                    outcome = PreloadOutcome.Skipped;
                    outcomeReason = hitlAction != null ? hitlAction.Variant.ToString() : "bot detection";
                }
                else if (ReadableContentExtractor.IsEmptyArticleShell(result.Html))
                {
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                        _logger.LogDebug(
                            "Domain {Origin} needs JS rendering, skipping future HTTP pre-loads",
                            origin);
                    }

                    outcome = PreloadOutcome.Skipped;
                    outcomeReason = "needs JS";
                }
                else if (!CachingPageLoader.HasSufficientContent(result.Html))
                {
                    // Page passed the article shell check but has too little visible text.
                    // This catches JS shells without article markup, empty pages, and pages
                    // where content is loaded dynamically. Mark the domain as needing JS
                    // so future preloads for this domain are skipped.
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }

                    _logger.LogDebug(
                        "Skipping cache for preloaded URL with insufficient content: {Url}",
                        url);

                    outcome = PreloadOutcome.Skipped;
                    outcomeReason = "insufficient content";
                }
                else if (ReadableContentExtractor.IsArticlePage(result.Html) &&
                         !ReadableContentExtractor.HasExtractableContent(result.Html))
                {
                    // Page looks like an article (has article indicators) but has no
                    // extractable article content. This catches JS-heavy sites like NYT
                    // that return a shell with nav/header text but no article body.
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }

                    _logger.LogDebug(
                        "Skipping cache for preloaded URL with no extractable article content: {Url}",
                        url);

                    outcome = PreloadOutcome.Skipped;
                    outcomeReason = "no article body";
                }
                else if (result.Url != null && IsRedirectedUrl(url, result.Url))
                {
                    // Server redirected to a different page (e.g., paywalled article → section page).
                    // Do NOT cache under the original URL — the content doesn't match the request.
                    _logger.LogDebug(
                        "Skipping cache for redirected URL: requested={RequestUrl}, redirected={FinalUrl}",
                        url,
                        result.Url);

                    outcome = PreloadOutcome.Skipped;
                    outcomeReason = "redirected";
                }
                else if (ReadableContentExtractor.HasPaywallElements(result.Html))
                {
                    // Paywall gate detected — don't cache truncated preview content.
                    // Mark domain as needing browser (browser with cookies).
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }

                    _logger.LogDebug("Skipping cache for paywalled content: {Url}", url);

                    outcome = PreloadOutcome.Skipped;
                    outcomeReason = "paywall";
                }
                else if (IsPaywalledDomain(url) && !CachingPageLoader.HasSufficientContent(result.Html, MinPaywalledWordCount))
                {
                    // Paywalled domain passed basic checks but has too few words —
                    // a thin preview slipped through. Mark the domain as needsJs so
                    // future paywalled URLs route to the browser path instead.
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }

                    _logger.LogDebug(
                        "Skipping cache for paywalled domain with insufficient content (<{MinWords} words): {Url}",
                        MinPaywalledWordCount,
                        url);

                    outcome = PreloadOutcome.Skipped;
                    outcomeReason = "paywall preview";
                }
                else
                {
                    _currentStage = PreloadStage.PersistingCache;
                    _cache.Put(url, result);

                    if (IsPaywalledDomain(url))
                    {
                        Interlocked.Increment(ref _paywalledPreloadCount);
                    }

                    _logger.LogDebug("Pre-loaded and cached: {Url}", url);

                    // Warm the PageBuildCache: extract links and readable content
                    // so navigation to this URL skips extraction entirely.
                    await TryBuildAndCachePageAsync(url, result, cancellationToken).ConfigureAwait(false);

                    // Bridge to article content cache: extract article and persist
                    // so collection items served from article cache on navigation.
                    _currentStage = PreloadStage.ExtractingContent;
                    await TryExtractAndCacheArticleAsync(url, result.Html, cancellationToken).ConfigureAwait(false);

                    // Clear the sticky HITL badge if this success is on the same
                    // origin as the last verdict (workspace-0b9s QA #2). Without
                    // this, one tripped URL keeps the "⏸ verify at {domain}" badge
                    // on for the rest of the session.
                    ClearBlockedActionForOriginIfMatches(url);

                    outcome = PreloadOutcome.Cached;
                }
            }
            else
            {
                _logger.LogDebug("Pre-load failed for {Url}: {Error}", url, result.ErrorMessage);

                // Paywalled domains often return 4xx/5xx for unauthenticated HTTP requests
                // even with cookies attached. Mark the domain as needsJs so subsequent
                // URLs route through the browser path.
                if (IsPaywalledDomain(url))
                {
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }
                }

                outcome = PreloadOutcome.Failed;
                outcomeReason = result.ErrorMessage;
            }

            tcs.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            _logger.LogDebug(ex, "Pre-load error for {Url}", url);
            outcome = PreloadOutcome.Failed;
            outcomeReason = ex.Message;
        }
        finally
        {
            sw.Stop();
            _currentStage = PreloadStage.Idle;
            AppendHistory(url, outcome, sw.ElapsedMilliseconds, outcomeReason);
            _inFlight.TryRemove(normalizedUrl, out _);
            NotifyProgressChanged();

            // If this domain was marked as needsJs and browser preloading is available,
            // trigger a queue rebuild so remaining URLs for this domain get routed
            // through the browser path instead of being skipped.
            if (IsDomainNeedsJs(url) && CanBrowserPreload)
            {
                _logger.LogInformation(
                    "Domain {Url} marked as needsJs with browser available — triggering queue rebuild",
                    UrlNormalizer.GetOrigin(url));
                RequestQueueRebuild();
            }
        }
    }

    private async Task BrowserPreloadUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (IsPaywalledDomain(url) && _paywalledPreloadCount >= _config.MaxPaywalledPreloads)
        {
            _logger.LogDebug(
                "Browser preload skipped — paywalled limit reached ({Count}/{Max}): {Url}",
                _paywalledPreloadCount,
                _config.MaxPaywalledPreloads,
                url);
            return;
        }

        if (!IsPaywalledDomain(url) && _generalBrowserPreloadCount >= _config.MaxBrowserPreloads)
        {
            _logger.LogDebug(
                "Browser preload skipped — general limit reached ({Count}/{Max}): {Url}",
                _generalBrowserPreloadCount,
                _config.MaxBrowserPreloads,
                url);
            return;
        }

        // Register in-flight BEFORE navigation, mirroring the HTTP path
        // (PreloadUrlAsync). This lets a foreground Enter for the same URL await
        // the browser nav via WaitForInFlightAsync instead of re-navigating, and
        // de-duplicates concurrent preloads. Every exit path must complete the TCS
        // or the foreground would block up to its cap and DequeueNext would treat
        // the URL as permanently in-flight.
        var normalizedUrl = UrlNormalizer.Normalize(url);
        var tcs = new TaskCompletionSource<PageLoadResult>();
        if (_inFlight.GetOrAdd(normalizedUrl, tcs.Task) != tcs.Task)
        {
            _logger.LogDebug("Skipping duplicate browser pre-load for {Url}", url);
            return;
        }

        // workspace-7xw0 history parity with the HTTP path.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = PreloadOutcome.Failed;
        string? outcomeReason = null;
        var fellBackToHttp = false;

        // HTTP fallback used when the browser transport is unavailable or a nav
        // fails. We must complete + remove the browser TCS BEFORE delegating, or
        // PreloadUrlAsync's own _inFlight.GetOrAdd would observe the browser TCS,
        // log "duplicate", and return without fetching. The HTTP path registers
        // its own in-flight entry and runs the full caching/HITL gauntlet.
        async Task FallbackToHttpAsync(string reason)
        {
            if (fellBackToHttp || !_config.PreloadUseBrowser)
            {
                return;
            }

            fellBackToHttp = true;
            _inFlight.TryRemove(normalizedUrl, out _);
            tcs.TrySetResult(PageLoadResult.Failure(reason));
            outcome = PreloadOutcome.Skipped;
            outcomeReason = reason;
            _logger.LogDebug("Browser preload falling back to HTTP for {Url}: {Reason}", url, reason);
            await PreloadUrlAsync(url, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (_browserSession == null)
            {
                await FallbackToHttpAsync("browser session unavailable").ConfigureAwait(false);
                return;
            }

            _currentlyFetchingUrl = url;
            Interlocked.Exchange(ref _currentlyFetchingStartedAtTicks, DateTime.UtcNow.Ticks);
            _currentStage = PreloadStage.Fetching;
            _logger.LogDebug("Browser pre-loading: {Url}", url);

            // Lazily create background page on first use.
            // Context may not exist yet if browser warmup is still running (fire-and-forget).
            // Retry once after a short wait to handle the race condition.
            if (_backgroundPage == null)
            {
                _backgroundPage = await _browserSession.CreateBackgroundPageAsync().ConfigureAwait(false);
                if (_backgroundPage == null)
                {
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    _backgroundPage = await _browserSession.CreateBackgroundPageAsync().ConfigureAwait(false);
                }
            }

            if (_backgroundPage == null)
            {
                _logger.LogWarning("Browser context not available for background preload of {Url}", url);
                await FallbackToHttpAsync("background page unavailable").ConfigureAwait(false);
                return;
            }

            // Re-minimize before every preload navigation as well — creating a
            // new tab raises the window, and even if the page is reused
            // a stray prior interaction may have brought it to the front.
            try
            {
                await _browserSession.MinimizeWindowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to minimize before preload navigation (non-fatal)");
            }

            await _backgroundPage.GotoAsync(url, new PageGotoOptions
            {
                Timeout = 15000,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            }).ConfigureAwait(false);

            // Pre-fetch must be quiet (workspace-8rqh): Chromium raises the
            // window on navigation, even for a background tab in the persistent
            // context. Re-minimize after every navigation so the user's
            // foreground browser doesn't pop up during preload.
            try
            {
                await _browserSession.MinimizeWindowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to minimize after preload navigation (non-fatal)");
            }

            // workspace-1rfd: label the prefetch tab so a user who looks at the
            // browser mid-cache understands what they're seeing.
            try
            {
                var (done, total) = CachedProgressSnapshot();
                await _backgroundPage.EvaluateAsync<string>(
                    PrefetchBadgeScript.Apply,
                    new { done, total }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Prefetch badge apply failed (non-fatal)");
            }

            // Wait for JS content to render (article paragraphs or sufficient DOM size)
            try
            {
                await _backgroundPage.WaitForFunctionAsync(
                    @"() => {
                        if (document.querySelector('[role=""main""] p, article p, .entry-content p, .post-content p')) return true;
                        if (document.querySelector('[data-testid=""storyContent""] p, .StoryBodyCompanionColumn p')) return true;
                        return document.body && document.body.innerHTML.length > 5000;
                    }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 4000 }).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Browser preload content render wait timed out for {Url}", url);
            }
            catch (PlaywrightException)
            {
                _logger.LogDebug("Browser preload content render wait failed for {Url}", url);
            }

            await PageLoader.DismissOverlaysAsync(_backgroundPage, _logger).ConfigureAwait(false);

            var html = await _backgroundPage.ContentAsync().ConfigureAwait(false);
            var finalUrl = _backgroundPage.Url;

            _currentStage = PreloadStage.Detecting;

            // Typed HITL detection on the browser preload path (workspace-0b9s QA #3).
            // Previously this branch only called IsBotChallengePage(html), set the
            // circuit breaker, and returned silently — so the NYT scenario named in
            // the bead (browser-preload-routed paywalled domain hits a CAPTCHA / login
            // wall) never surfaced a verdict to the status bar. Now we run the same
            // HumanActionDetector the HTTP path uses and publish the result.
            var browserDetection = HumanActionDetector.Detect(html, finalUrl, statusCode: 0);
            if (browserDetection != null || PageLoader.IsBotChallengePage(html))
            {
                var origin = UrlNormalizer.GetOrigin(url);
                if (origin != null)
                {
                    _circuitBrokenDomains[origin] = DateTime.UtcNow;
                }

                _logger.LogWarning(
                    "Human action required during browser preload ({Variant}): {Url}",
                    browserDetection?.Variant.ToString() ?? "BotDetection",
                    url);

                if (browserDetection != null)
                {
                    _lastBlockedAction = browserDetection;
                    NotifyProgressChanged();
                }

                // Complete in-flight with a non-Success result so a foreground
                // WaitForInFlightAsync returns cleanly and falls through to its own
                // load path instead of blocking. Do NOT fall back to HTTP here —
                // a HITL wall is a genuine block, not a transport failure.
                tcs.TrySetResult(PageLoadResult.Failure(
                    browserDetection?.Variant.ToString() ?? "bot detection"));
                outcome = PreloadOutcome.Skipped;
                outcomeReason = browserDetection?.Variant.ToString() ?? "bot detection";
                return;
            }

            // Skip paywall element check for browser preloads — authenticated pages
            // still contain paywall CSS classes (gateway, meter-) in the DOM even though
            // the gate is inactive. The content sufficiency check below catches truly
            // paywalled content (too few words).
            if (!CachingPageLoader.HasSufficientContent(html, MinPaywalledWordCount))
            {
                _logger.LogDebug("Browser preload content below threshold for {Url}", url);
                tcs.TrySetResult(PageLoadResult.Failure("insufficient content"));
                outcome = PreloadOutcome.Skipped;
                outcomeReason = "insufficient content";
                return;
            }

            // Cache the rendered HTML. Tag the transport as Browser and warm the
            // PageBuildCache for navigation parity with the HTTP path — otherwise a
            // foreground Enter would re-extract (SearchCommandHandler /
            // StrategyChooserHandler / PageLoadPipeline all read TryGetBuildCache).
            var metadata = ExtractMetadata(html);
            var result = PageLoadResult.Successful(finalUrl, html, metadata, FetchMethod.Browser);
            _currentStage = PreloadStage.PersistingCache;
            _cache.Put(url, result);
            await TryBuildAndCachePageAsync(url, result, cancellationToken).ConfigureAwait(false);

            // Extract article content for the article cache
            _currentStage = PreloadStage.ExtractingContent;
            if (_contentExtractor != null && _articleContentCache != null)
            {
                try
                {
                    var readable = await _contentExtractor.ExtractAsync(html, url, cancellationToken).ConfigureAwait(false);
                    if (readable != null && !readable.IsPaywalled)
                    {
                        var article = new ExtractedArticle
                        {
                            Title = readable.Title,
                            CleanedText = readable.CleanedText,
                            Author = readable.Author,
                            Url = url,
                            WordCount = readable.WordCount,
                            PublishedDate = readable.PublishedDate,
                        };

                        await _articleContentCache.PutAsync(url, article, cancellationToken).ConfigureAwait(false);
                        _articleCachedUrls[UrlNormalizer.Normalize(url)] = url;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Article extraction failed for browser-preloaded {Url}", url);
                }
            }

            if (IsPaywalledDomain(url))
            {
                Interlocked.Increment(ref _paywalledPreloadCount);
            }
            else
            {
                Interlocked.Increment(ref _generalBrowserPreloadCount);
            }

            _logger.LogInformation("Browser pre-loaded and cached: {Url}", url);

            // Clear sticky HITL badge on browser-path success too (workspace-0b9s QA #2):
            // the same clear-on-same-origin-success rule applies regardless of whether
            // the preload that succeeds went through HTTP or the browser path.
            ClearBlockedActionForOriginIfMatches(url);

            tcs.TrySetResult(result);
            outcome = PreloadOutcome.Cached;
            outcomeReason = null;
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Browser preload failed for {Url}", url);

            // Page may have crashed — null it so next call creates a fresh one
            _backgroundPage = null;

            // A transient browser nav failure should not drop the URL — give it
            // one HTTP attempt with full in-flight + caching semantics.
            await FallbackToHttpAsync("browser nav failed").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Browser preload error for {Url}", url);
            tcs.TrySetResult(PageLoadResult.Failure(ex.Message));
            outcome = PreloadOutcome.Failed;
            outcomeReason = ex.Message;
        }
        finally
        {
            // Backstop: if any branch above forgot to complete the TCS, complete it
            // now so a foreground waiter never hangs and DequeueNext doesn't treat
            // the URL as permanently in-flight.
            tcs.TrySetResult(PageLoadResult.Failure("browser preload did not complete"));
            _inFlight.TryRemove(normalizedUrl, out _);
            sw.Stop();
            _currentStage = PreloadStage.Idle;
            AppendHistory(url, outcome, sw.ElapsedMilliseconds, outcomeReason);
            _currentlyFetchingUrl = null;
            Interlocked.Exchange(ref _currentlyFetchingStartedAtTicks, long.MinValue);
            NotifyProgressChanged();
        }
    }

    /// <summary>
    /// Extracts links and readable content from pre-loaded HTML and stores a
    /// PageBuildCache alongside the HTML in IPageCache. This means navigating to
    /// a preloaded URL skips link extraction, tree building, and content extraction
    /// entirely — the page is rebuilt from cached inputs in ~1ms.
    /// </summary>
    private async Task TryBuildAndCachePageAsync(
        string url,
        PageLoadResult result,
        CancellationToken cancellationToken)
    {
        if (_linkExtractor == null)
        {
            return;
        }

        try
        {
            var links = await _linkExtractor.ExtractLinksAsync(
                result.Html, result.Url ?? url, cancellationToken).ConfigureAwait(false);

            if (links.Count == 0)
            {
                return;
            }

            ReadableContent? readable = null;
            if (_contentExtractor != null)
            {
                readable = await _contentExtractor.ExtractAsync(
                    result.Html, result.Url ?? url, cancellationToken).ConfigureAwait(false);
            }

            var signals = PageSignalExtractor.Extract(result.Html);
            var preloadContentLinks = links.Count(l => l.Type == Domain.Enums.Browser.LinkType.Content);
            var (classification, classificationScore) = PageClassifier.ClassifyScored(signals, preloadContentLinks, result.Url ?? url);

            // Quality gate: don't cache build results that would serve bad pages.
            // HTTP-fetched JS shells often have nav links but no article content,
            // causing misclassification as LinkList for article URLs.
            if (classification == PageClassification.LinkList && !PageClassifier.IsSectionUrlPattern(url))
            {
                _logger.LogDebug(
                    "Skipping build cache for non-section URL classified as LinkList: {Url}",
                    url);
                return;
            }

            if (readable == null && classification != PageClassification.LinkList)
            {
                _logger.LogDebug(
                    "Skipping build cache for page with no readable content (classification={Classification}): {Url}",
                    classification,
                    url);
                return;
            }

            var buildCache = new PageBuildCache
            {
                Links = links,
                ReadableContent = readable,
                Metadata = result.Metadata ?? new PageMetadata { Title = "Untitled" },
                FinalUrl = result.Url ?? url,
                Classification = classification,
                ClassificationVersion = PageClassifier.ClassificationVersion,
                ExtractionVersion = LinkExtractor.ExtractionVersion,
                ClassificationScore = classificationScore,
            };

            _cache.PutBuildCache(url, buildCache);
            _logger.LogDebug(
                "PageBuildCache warmed from preload: {Url} ({LinkCount} links)",
                url,
                links.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PageBuildCache warm failed for preloaded {Url}", url);
        }
    }

    /// <summary>
    /// Extracts article content from pre-loaded HTML and stores it in the persistent
    /// article content cache. This bridges the background preload (IPageCache) to the
    /// article cache (IArticleContentCache) so that collection items can be served
    /// directly from the article cache on navigation, skipping network I/O entirely.
    /// </summary>
    private async Task TryExtractAndCacheArticleAsync(string url, string html, CancellationToken cancellationToken)
    {
        if (_contentExtractor == null || _articleContentCache == null)
        {
            return;
        }

        // Never article-cache content from paywalled domains via HTTP.
        // These need browser with cookies for full content; HTTP fetch
        // only gets truncated preview that may not trigger paywall detection.
        if (IsPaywalledDomain(url))
        {
            _logger.LogDebug("Skipping article cache for paywalled domain: {Url}", url);
            return;
        }

        try
        {
            var readable = await _contentExtractor.ExtractAsync(html, url, cancellationToken).ConfigureAwait(false);
            if (readable == null || readable.IsPaywalled)
            {
                return;
            }

            var article = new ExtractedArticle
            {
                Title = readable.Title,
                CleanedText = readable.CleanedText,
                Author = readable.Author,
                Url = url,
                WordCount = readable.WordCount,
                PublishedDate = readable.PublishedDate,
            };

            await _articleContentCache.PutAsync(url, article, cancellationToken).ConfigureAwait(false);
            _articleCachedUrls[UrlNormalizer.Normalize(url)] = url;
            _logger.LogDebug(
                "Article content cached from preload: {Url} ({Words} words)",
                url,
                article.WordCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Article extraction failure should not break preloading
            _logger.LogDebug(ex, "Article extraction/cache failed for preloaded {Url}", url);
        }
    }

    private async Task<PageLoadResult> HttpFetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return PageLoadResult.Failure(
                    $"HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode);
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            var metadata = ExtractMetadata(html);

            return PageLoadResult.Successful(finalUrl, html, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PageLoadResult.Failure(ex.Message);
        }
    }
}
