// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Podcast.Cache;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Encapsulates the page-loading pipeline: article cache lookup, build cache,
/// HTTP/browser fetch, bot challenge handling, content quality retry, paywall
/// retry, auto-login fallback, and headless-to-headed escalation.
///
/// Extracted from <see cref="BrowserOrchestrator"/> to reduce its size and
/// separate orchestration concerns from page-loading concerns.
/// </summary>
public class PageLoadPipeline
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IRssFeedDetector _feedDetector;
    private readonly IPageRenderer _renderer;
    private readonly NavigationService _navigationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Configuration.BrowserConfiguration _browserConfig;
    private readonly IBrowserSessionControl _browserSession;
    private readonly IPageCache _pageCache;
    private readonly IPreloadService _preloadService;
    private readonly ICookieManager _cookieManager;
    private readonly ILogger<PageLoadPipeline> _logger;

    // Lazily resolved article content cache (may not be registered if podcast services are disabled)
    private IArticleContentCache? _articleContentCache;
    private bool _articleContentCacheResolved;

    // Lazily resolved hierarchy services (may not need to construct until first use)
    private IHierarchyConfigStore? _hierarchyConfigStore;
    private bool _hierarchyConfigStoreResolved;

    public PageLoadPipeline(
        IPageLoader pageLoader,
        ILinkExtractor linkExtractor,
        INavigationTreeBuilder treeBuilder,
        IReadableContentExtractor contentExtractor,
        IRssFeedDetector feedDetector,
        IPageRenderer renderer,
        NavigationService navigationService,
        IServiceScopeFactory scopeFactory,
        IBrowserSessionControl browserSession,
        IPageCache pageCache,
        IPreloadService preloadService,
        ICookieManager cookieManager,
        Configuration.BrowserConfiguration browserConfig,
        ILogger<PageLoadPipeline> logger)
    {
        _pageLoader = pageLoader;
        _linkExtractor = linkExtractor;
        _treeBuilder = treeBuilder;
        _contentExtractor = contentExtractor;
        _feedDetector = feedDetector;
        _renderer = renderer;
        _navigationService = navigationService;
        _scopeFactory = scopeFactory;
        _browserSession = browserSession;
        _pageCache = pageCache;
        _preloadService = preloadService;
        _cookieManager = cookieManager;
        _browserConfig = browserConfig;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full page-loading pipeline for a URL: article cache, build cache,
    /// network fetch, bot challenge, content quality retry, paywall retry, auto-login,
    /// and headless-to-headed escalation.
    /// </summary>
    /// <param name="url">URL to load.</param>
    /// <param name="reportStage">Optional callback to report loading stage progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Pipeline result with the assembled page, fetch method, and build cache.</returns>
    public async Task<PageLoadPipelineResult> LoadAsync(
        string url,
        Action<string>? reportStage,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading page: {Url}", url);
        _logger.LogDebug("BrowserConfig.Headless = {Headless}", _browserConfig.Headless);

#pragma warning disable S1854 // Required by definite assignment rules
        var fetchMethod = FetchMethod.Http;
#pragma warning restore S1854

        // Article content cache bridge: when navigating from a collection (Reading List),
        // check the persistent article cache before doing any network I/O.
        // This avoids re-fetching articles that were already extracted for podcast generation.
        if (_navigationService.HasCollectionReturnPoint)
        {
            var articleCache = ResolveArticleContentCache();
            if (articleCache != null)
            {
                try
                {
                    var cachedArticle = await articleCache.TryGetAsync(url, cancellationToken);
                    if (cachedArticle != null)
                    {
                        // Quality gate: reject cached articles from paywalled domains
                        // that may contain truncated preview content
                        if (_browserConfig.IsPaywalledDomain(url) && cachedArticle.WordCount < 200)
                        {
                            _logger.LogInformation(
                                "Evicting low-quality cached article for paywalled domain: {Url} ({Words} words)",
                                url,
                                cachedArticle.WordCount);
                            await articleCache.RemoveAsync(url, cancellationToken);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Article content cache hit for collection item: {Url} ({Words} words)",
                                url,
                                cachedArticle.WordCount);

                            return new PageLoadPipelineResult
                            {
                                Page = BuildPageFromCachedArticle(cachedArticle, url),
                                FetchMethod = FetchMethod.Cached,
                                BuildResult = null,
                            };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Article content cache lookup failed for {Url}, falling through to normal load", url);
                }
            }
        }

        // Fast path: if we have cached build results (extracted links, hierarchy, content),
        // rebuild the page from those without re-parsing HTML or re-running AI analysis.
        _logger.LogInformation("LoadAsync: checking build cache for {Url}", url);
        var buildCache = _pageCache.TryGetBuildCache(url);
        if (buildCache != null)
        {
            // Quality gate: reject build caches with stale classification logic.
            // When PageClassifier rules change, old caches may have wrong classifications.
            if (buildCache.ClassificationVersion != PageClassifier.ClassificationVersion)
            {
                _logger.LogInformation(
                    "LoadAsync: rejecting stale build cache (classification v{Old} != v{New}): {Url}",
                    buildCache.ClassificationVersion,
                    PageClassifier.ClassificationVersion,
                    url);
                _pageCache.Remove(url);
            }

            // Quality gate: reject build caches that misclassified article URLs as LinkList.
            // HTTP-fetched JS shells often have nav links but no article content, producing
            // a bad LinkList classification that persists in cache.
            else if (buildCache.Classification == PageClassification.LinkList
                && !PageClassifier.IsSectionUrlPattern(url))
            {
                _logger.LogInformation(
                    "LoadAsync: rejecting stale build cache (LinkList for non-section URL): {Url}", url);
                _pageCache.Remove(url);
            }
            else
            {
                _logger.LogInformation("LoadAsync: build cache HIT for {Url}, skipping extraction", url);

                // Refresh TTL so link-list pages stay cached across revisits
                if (buildCache.Classification == PageClassification.LinkList)
                {
                    _pageCache.ApplyLinkListTtl(url);
                }

                return new PageLoadPipelineResult
                {
                    Page = await RebuildFromBuildCacheAsync(buildCache).ConfigureAwait(false),
                    FetchMethod = FetchMethod.Cached,
                    BuildResult = null,
                };
            }
        }

        var htmlCached = _pageCache.Contains(url);
        _logger.LogInformation("LoadAsync: HTML cache {Result} for {Url}", htmlCached ? "HIT" : "MISS", url);
        if (!htmlCached)
        {
            reportStage?.Invoke("Checking pre-load...");

            // Check if preload service has an in-flight fetch for this URL
            var inFlightResult = await _preloadService.WaitForInFlightAsync(
                url, TimeSpan.FromSeconds(3), cancellationToken);
            if (inFlightResult != null && inFlightResult.Success)
            {
                _logger.LogInformation("Using in-flight preload result for {Url}", url);
                fetchMethod = FetchMethod.Cached;
                var (inFlightPage, inFlightBuild) = await BuildPageAsync(inFlightResult, url, cancellationToken);
                if (inFlightPage.HasReadableContent())
                {
                    return new PageLoadPipelineResult
                    {
                        Page = inFlightPage,
                        FetchMethod = fetchMethod,
                        BuildResult = inFlightBuild,
                    };
                }

                _logger.LogDebug("In-flight preload result had no readable content, proceeding with normal load");
            }
        }

        // Browser-first: always use the browser for primary navigation.
        // The HTTP fast-path caused recurring issues with Cloudflare challenges,
        // JS shells, and dynamic sites that need rendering. The browser (Patchright)
        // handles all of these transparently. Background preloading still uses HTTP.
        reportStage?.Invoke("Loading via browser...");

        var loadResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceBrowser = true },
            cancellationToken);

        _logger.LogDebug(
            "LoadAsync initial load: url={Url}, success={Success}, method={Method}, contentLength={Length}",
            url,
            loadResult.Success,
            loadResult.FetchMethod,
            loadResult.Html?.Length ?? 0);

        if (!loadResult.Success)
        {
            throw new InvalidOperationException($"Failed to load page: {loadResult.ErrorMessage}");
        }

        fetchMethod = loadResult.FetchMethod;

        reportStage?.Invoke("Extracting content...");
        PageBuildCache? lastBuildResult;
        Page page;
        (page, lastBuildResult) = await BuildPageAsync(loadResult, url, cancellationToken);

        // Bot challenge handling: if browser returned a challenge page in headed mode,
        // wait for the user to solve it in the visible browser window (interactive, must be synchronous)
        var challengeResult = await HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken);
        if (challengeResult != null)
        {
            fetchMethod = challengeResult.FetchMethod;
            loadResult = challengeResult;
            _pageCache.Put(url, challengeResult);
            (page, lastBuildResult) = await BuildPageAsync(challengeResult, url, cancellationToken);
        }

        // Content-quality fallback: if no content at all from HTTP/cached page, retry with browser
        // synchronously — the user has nothing to interact with otherwise.
        var browserAvailable = (_browserSession as IBrowserSession)?.IsBrowserAvailable ?? false;
        var hasLinks = page.LinkTree != null && page.LinkTree.TotalLinks > 0;

        if (!page.HasReadableContent() && !hasLinks && loadResult.FetchMethod != FetchMethod.Browser && browserAvailable)
        {
            _logger.LogInformation(
                "No readable content from {FetchMethod} page, retrying with browser: {Url}",
                loadResult.FetchMethod,
                url);

            _pageCache.Remove(url);
            reportStage?.Invoke("Retrying with browser...");

            var retryResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true },
                cancellationToken);

            if (retryResult.Success)
            {
                fetchMethod = retryResult.FetchMethod;
                (page, lastBuildResult) = await BuildPageAsync(retryResult, url, cancellationToken);
            }
        }

        _logger.LogInformation("Page loaded: {Title} - {LinkCount} links, {HasReadable} readable",
            page.Metadata.Title,
            page.LinkTree?.TotalLinks ?? 0,
            page.HasReadableContent() ? "has" : "no");

        // Determine if quality improvement retries are needed.
        // If so, return the current page immediately and schedule retries in the background.
        // The user can interact with the (possibly truncated) content while retries run.
        Task<PageLoadPipelineResult>? qualityRetryTask = null;
        var needsQualityRetry = NeedsQualityRetry(page, fetchMethod, loadResult, browserAvailable);

        if (needsQualityRetry)
        {
            // Cache what we have so far (will be replaced if retry succeeds)
            StoreBuildCache(url, page, lastBuildResult);

            var capturedFetchMethod = fetchMethod;
            var capturedLoadResult = loadResult;
            qualityRetryTask = Task.Run(() =>
                RunQualityRetriesAsync(url, page, capturedFetchMethod, capturedLoadResult, browserAvailable, cancellationToken),
                cancellationToken);
        }
        else
        {
            StoreBuildCache(url, page, lastBuildResult);
        }

        return new PageLoadPipelineResult
        {
            Page = page,
            FetchMethod = fetchMethod,
            BuildResult = lastBuildResult,
            QualityRetryTask = qualityRetryTask,
        };
    }

    /// <summary>
    /// Builds a Page entity from a PageLoadResult, extracting links, tree, and readable content.
    /// Returns both the page and the build cache snapshot for later storage.
    /// Used by ForceRefreshAsync and InteractiveRefreshAsync in BrowserOrchestrator.
    /// </summary>
    public async Task<(Page Page, PageBuildCache? BuildResult)> BuildPageAsync(
        PageLoadResult loadResult,
        string requestedUrl,
        CancellationToken cancellationToken)
    {
        var metadata = loadResult.Metadata ?? new PageMetadata { Title = "Untitled" };

        // Use the final URL after redirects for Page.Url so that status bar,
        // refresh, and cache lookups all use the correct URL.
        var finalUrl = loadResult.Url ?? requestedUrl;
        var page = Page.Create(finalUrl, loadResult.Html, metadata);

        var links = await _linkExtractor.ExtractLinksAsync(loadResult.Html, loadResult.Url ?? requestedUrl, cancellationToken);

        // Classify the page using signal-scored approach
        var signals = PageSignalExtractor.Extract(loadResult.Html);
        var contentLinkCount = links.Count(l => l.Type == Domain.Enums.Browser.LinkType.Content);
        var (classification, classificationScore) = PageClassifier.ClassifyScored(signals, contentLinkCount, finalUrl);
        page.SetClassification(classification);
        _logger.LogInformation(
            "Page classified as {Classification} (score={Score}): {Url} (contentLinks={ContentLinks}, og:type={OgType}, ld+json={LdJson}, articles={Articles})",
            classification,
            classificationScore,
            finalUrl,
            contentLinkCount,
            signals.OgType ?? "none",
            signals.LdJsonType ?? "none",
            signals.ArticleContainerCount);

        // Try saved hierarchy config, then fall back to document-order
        NavigationTree tree;
        var hierarchyConfig = await TryGetOrAnalyzeHierarchyAsync(links, finalUrl);
        if (hierarchyConfig != null && hierarchyConfig.Kind == LayoutKind.RssFeed
            && !string.IsNullOrEmpty(hierarchyConfig.RssFeedUrl))
        {
            // Saved RSS layout: fetch feed and build tree from feed items
            var feedItems = await _feedDetector.ParseFeedAsync(hierarchyConfig.RssFeedUrl, cancellationToken);
            if (feedItems.Count > 0)
            {
                tree = await _treeBuilder.BuildTreeAsync(feedItems, cancellationToken);
                _navigationService.SetAiHierarchy(false);
                _navigationService.SetStatusMessage($"RSS feed · {feedItems.Count} articles");
            }
            else
            {
                // Feed unavailable — fallback to document-order
                tree = await _treeBuilder.BuildTreeAsync(links, cancellationToken);
                _navigationService.SetAiHierarchy(false);
                _navigationService.SetStatusMessage("Saved RSS feed unavailable · Ctrl+L to reconfigure");
            }
        }
        else if (hierarchyConfig != null)
        {
            tree = await _treeBuilder.BuildTreeAsync(links, hierarchyConfig, cancellationToken);
            _navigationService.SetAiHierarchy(true);
        }
        else
        {
            tree = await _treeBuilder.BuildTreeAsync(links, cancellationToken);
            _navigationService.SetAiHierarchy(false);
        }

        page.SetLinkTree(tree);

        // Skip content extraction for link list pages -- only the link tree matters.
        ReadableContent? readable = null;
        if (classification != PageClassification.LinkList)
        {
            readable = await _contentExtractor.ExtractAsync(loadResult.Html, loadResult.Url ?? requestedUrl, cancellationToken);
            if (readable != null)
            {
                page.SetReadableContent(readable);
            }
            else if (classification == PageClassification.Article)
            {
                _logger.LogWarning(
                    "Content extraction returned null for Article-classified page: {Url} (htmlLength={HtmlLength})",
                    finalUrl,
                    loadResult.Html?.Length ?? 0);
            }
        }

        if (!string.Equals(requestedUrl, loadResult.Url, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "URL redirect detected: requested={RequestedUrl}, final={FinalUrl}",
                requestedUrl,
                loadResult.Url);
        }

        _logger.LogDebug(
            "BuildPage: requestedUrl={RequestedUrl}, loadResultUrl={FinalUrl}, fetchMethod={Method}, hasReadable={HasReadable}, paragraphs={Count}, htmlLength={HtmlLength}",
            requestedUrl,
            loadResult.Url,
            loadResult.FetchMethod,
            page.HasReadableContent(),
            page.ReadableContent?.Paragraphs.Count ?? 0,
            loadResult.Html?.Length ?? 0);

        // Detect RSS/Atom feeds from page HTML
        List<FeedInfo>? detectedFeeds = null;
        if (!string.IsNullOrEmpty(loadResult.Html))
        {
            var feeds = _feedDetector.DetectFeeds(loadResult.Html, finalUrl);
            if (feeds != null && feeds.Count > 0)
            {
                detectedFeeds = feeds;
                page.SetDetectedFeeds(feeds);
            }
        }

        // Capture build inputs for StoreBuildCache
        var buildResult = new PageBuildCache
        {
            Links = links,
            HierarchyConfig = hierarchyConfig,
            ReadableContent = readable,
            Metadata = metadata,
            FinalUrl = finalUrl,
            Classification = classification,
            ClassificationVersion = PageClassifier.ClassificationVersion,
            ClassificationScore = classificationScore,
            DetectedFeeds = detectedFeeds,
        };

        return (page, buildResult);
    }

    /// <summary>
    /// Rebuilds a Page from cached build results (extracted links, hierarchy, content).
    /// Creates a fresh NavigationTree with clean selection state.
    /// Used by NavigateToAsync for build cache hits.
    /// </summary>
    public async Task<Page> RebuildFromBuildCacheAsync(PageBuildCache cache)
    {
        var page = Page.Create(cache.FinalUrl, string.Empty, cache.Metadata);

        NavigationTree tree;
        if (cache.HierarchyConfig != null)
        {
            tree = await _treeBuilder.BuildTreeAsync(cache.Links, cache.HierarchyConfig).ConfigureAwait(false);
            _navigationService.SetAiHierarchy(true);
        }
        else
        {
            tree = await _treeBuilder.BuildTreeAsync(cache.Links).ConfigureAwait(false);
            _navigationService.SetAiHierarchy(false);
        }

        page.SetLinkTree(tree);
        page.SetClassification(cache.Classification);

        if (cache.ReadableContent != null)
        {
            page.SetReadableContent(cache.ReadableContent);
        }

        if (cache.DetectedFeeds is { Count: > 0 })
        {
            page.SetDetectedFeeds(cache.DetectedFeeds);
        }

        return page;
    }

    /// <summary>
    /// Handles bot challenge detection and polling in headed mode.
    /// Returns a resolved PageLoadResult if the challenge was solved, or null if not applicable.
    /// Used by InteractiveRefreshAsync in BrowserOrchestrator.
    /// </summary>
    public async Task<PageLoadResult?> HandleBotChallengeIfNeededAsync(
        string url,
        PageLoadResult loadResult,
        CancellationToken cancellationToken,
        bool? headlessOverride = null)
    {
        var headless = headlessOverride ?? _browserConfig.Headless;

        if (loadResult.FetchMethod != FetchMethod.Browser ||
            !PageLoader.IsBotChallengePage(loadResult.Html) ||
            headless)
        {
            return null;
        }

        _logger.LogWarning("Bot challenge detected in headed mode, waiting for user to resolve: {Url}", url);

        // Bring browser window to foreground so user can see and solve the challenge
        if (_browserSession is IBrowserSession challengeSession)
        {
            await challengeSession.RestoreWindowAsync();
        }

        _renderer.RenderChallenge(url);

        var challengeTimeout = TimeSpan.FromMinutes(2);
        var sw = Stopwatch.StartNew();
        var resolved = false;

        while (sw.Elapsed < challengeTimeout && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1500, cancellationToken);
            try
            {
                if (_browserSession is IBrowserSession session && session.IsBrowserAvailable)
                {
                    var page = await session.GetOrCreatePageAsync(headless);
                    var currentSource = await page.ContentAsync();
                    if (!PageLoader.IsBotChallengePage(currentSource))
                    {
                        _logger.LogInformation("Bot challenge resolved by user: {Url}", url);
                        resolved = true;
                        break;
                    }
                }
                else
                {
                    _logger.LogDebug("Browser session does not support page access, skipping challenge poll");
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Error polling challenge status");
                break;
            }
        }

        // Minimize browser after challenge resolved (or timed out)
        if (_browserSession is IBrowserSession resolvedSession)
        {
            await resolvedSession.MinimizeWindowAsync();
        }

        if (!resolved)
        {
            return null;
        }

        _pageCache.Remove(url);
        var retryResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, Headless = headless, ForceRefresh = true },
            cancellationToken);

        return retryResult.Success ? retryResult : null;
    }

    /// <summary>
    /// Determines whether the page needs background quality improvement retries.
    /// </summary>
    private bool NeedsQualityRetry(
        Page page,
        FetchMethod fetchMethod,
        PageLoadResult loadResult,
        bool browserAvailable)
    {
        if (!browserAvailable)
        {
            return false;
        }

        // Article with no readable content after browser fetch (JS rendering race)
        if (!page.HasReadableContent() &&
            page.Classification == PageClassification.Article &&
            fetchMethod == FetchMethod.Browser)
        {
            return true;
        }

        // Bot challenge in headless mode — retry headed so user can solve CAPTCHA
        if (fetchMethod == FetchMethod.Browser &&
            _browserConfig.Headless &&
            PageLoader.IsBotChallengePage(loadResult.Html ?? string.Empty))
        {
            return true;
        }

        // Empty LinkList — browser may have hit a challenge or JS rendering issue
        if (page.Classification == PageClassification.LinkList &&
            !page.HasLinks())
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs quality improvement retries in the background. Returns an improved result
    /// if any retry produces better content, otherwise returns the original page.
    /// </summary>
    private async Task<PageLoadPipelineResult> RunQualityRetriesAsync(
        string url,
        Page originalPage,
        FetchMethod fetchMethod,
        PageLoadResult loadResult,
        bool browserAvailable,
        CancellationToken cancellationToken)
    {
        var page = originalPage;
        PageBuildCache? lastBuildResult = null;

        try
        {
            // Auto-login fallback for paywalled content
            if (page.ReadableContent?.IsPaywalled == true)
            {
                try
                {
                    using var loginScope = _scopeFactory.CreateScope();
                    var autoLogin = loginScope.ServiceProvider.GetService<IAutoLoginService>();
                    if (autoLogin != null)
                    {
                        var host = new Uri(url).Host;
                        var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                            ? host[4..] : host;

                        if (await autoLogin.HasCredentialsAsync(domain, cancellationToken))
                        {
                            _logger.LogInformation(
                                "Quality retry: auto-login for {Domain}: {Url}", domain, url);

                            var loginResult = await autoLogin.LoginAsync(domain, cancellationToken);
                            if (loginResult.Success || loginResult.ManualLoginRequired)
                            {
                                if (loginResult.ManualLoginRequired)
                                {
                                    var manualLoginOk = await WaitForManualLoginAsync(url, domain, cancellationToken);
                                    if (!manualLoginOk)
                                    {
                                        _logger.LogInformation("Manual login was not completed for {Domain}", domain);
                                    }
                                }

                                _pageCache.Remove(url);
                                var autoLoginRetryResult = await _pageLoader.LoadAsync(
                                    new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true },
                                    cancellationToken);

                                if (autoLoginRetryResult.Success)
                                {
                                    fetchMethod = autoLoginRetryResult.FetchMethod;
                                    (page, lastBuildResult) = await BuildPageAsync(autoLoginRetryResult, url, cancellationToken);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-login attempt failed for {Url}", url);
                }
            }

            // Article content retry with extended wait
            if (!page.HasReadableContent() &&
                page.Classification == PageClassification.Article &&
                fetchMethod == FetchMethod.Browser &&
                browserAvailable)
            {
                _logger.LogInformation(
                    "Quality retry: article with no content after browser, retrying: {Url}", url);

                _pageCache.Remove(url);
                var retryResult = await _pageLoader.LoadAsync(
                    new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true, ForceBrowser = true },
                    cancellationToken);

                if (retryResult.Success)
                {
                    fetchMethod = retryResult.FetchMethod;
                    (page, lastBuildResult) = await BuildPageAsync(retryResult, url, cancellationToken);
                }
            }

            // Headless challenge fallback — applies to ALL classifications.
            // Cloudflare-protected homepages get classified as LinkList from URL alone,
            // but the HTML is a challenge page with no real content.
            if (loadResult.FetchMethod == FetchMethod.Browser &&
                _browserConfig.Headless &&
                PageLoader.IsBotChallengePage(loadResult.Html ?? string.Empty))
            {
                _logger.LogWarning(
                    "Quality retry: bot challenge in headless, retrying headed: {Url}", url);

                _pageCache.Remove(url);
                var headedResult = await _pageLoader.LoadAsync(
                    new PageLoadRequest { Url = url, Headless = false, ForceRefresh = true },
                    cancellationToken);

                if (headedResult.Success)
                {
                    fetchMethod = headedResult.FetchMethod;
                    (page, lastBuildResult) = await BuildPageAsync(headedResult, url, cancellationToken);
                }
            }

            // Guide user if still paywalled
            if (page.ReadableContent?.IsPaywalled == true)
            {
                var host = new Uri(url).Host;
                var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? host[4..] : host;
                _navigationService.SetStatusMessage(
                    $"Paywall detected on {domain}. Use :cred add {domain} to store credentials, or Shift+I to log in manually.");
            }

            StoreBuildCache(url, page, lastBuildResult);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Quality retry cancelled for {Url}", url);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quality retry failed for {Url}", url);
        }

        return new PageLoadPipelineResult
        {
            Page = page,
            FetchMethod = fetchMethod,
            BuildResult = lastBuildResult,
        };
    }

    /// <summary>
    /// Stores the build result in the page cache so repeat visits skip extraction.
    /// </summary>
    private void StoreBuildCache(string url, Page page, PageBuildCache? buildCache)
    {
        if (buildCache == null || page.LinkTree == null)
        {
            _logger.LogInformation(
                "StoreBuildCache: skipping for {Url} (buildCache={HasBuild}, linkTree={HasTree})",
                url,
                buildCache != null,
                page.LinkTree != null);
            return;
        }

        // Update with final state (fallbacks may have changed readable content)
        if (page.ReadableContent != buildCache.ReadableContent)
        {
            _logger.LogInformation("StoreBuildCache: readable content updated by fallback for {Url}", url);
            buildCache = buildCache with { ReadableContent = page.ReadableContent };
        }

        _pageCache.PutBuildCache(url, buildCache);

        // Also store under final URL (after redirects) so cache hits work
        // regardless of whether navigation uses the original or final URL
        if (!string.Equals(url, buildCache.FinalUrl, StringComparison.OrdinalIgnoreCase))
        {
            _pageCache.PutBuildCache(buildCache.FinalUrl, buildCache);
            _logger.LogInformation("StoreBuildCache: also stored under final URL {FinalUrl}", buildCache.FinalUrl);
        }

        // Use shorter TTL for link-list pages (content changes frequently)
        if (page.Classification == PageClassification.LinkList)
        {
            _pageCache.ApplyLinkListTtl(url);
            if (!string.Equals(url, buildCache.FinalUrl, StringComparison.OrdinalIgnoreCase))
            {
                _pageCache.ApplyLinkListTtl(buildCache.FinalUrl);
            }

            _logger.LogInformation("StoreBuildCache: applied LinkList TTL for {Url}", url);
        }

        _logger.LogInformation("StoreBuildCache: stored for {Url} ({LinkCount} links)", url, buildCache.Links.Count);
    }

    /// <summary>
    /// Builds a Page entity directly from a cached ExtractedArticle, skipping all network I/O.
    /// </summary>
    private Page BuildPageFromCachedArticle(Podcast.ExtractedArticle article, string url)
    {
        // Build minimal HTML for the page entity (required by Page.Create)
        var html = $"<html><head><title>{System.Net.WebUtility.HtmlEncode(article.Title)}</title></head>"
                 + $"<body><article>{System.Net.WebUtility.HtmlEncode(article.CleanedText)}</article></body></html>";

        var metadata = new PageMetadata { Title = article.Title };
        var page = Page.Create(url, html, metadata);

        // Build empty link tree (cached articles don't have link data)
        page.SetLinkTree(NavigationTree.Build(new List<LinkInfo>()));

        // Convert cached article text into ReadableContent
        var paragraphs = article.CleanedText
            .Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (paragraphs.Count == 0)
        {
            paragraphs.Add(article.CleanedText);
        }

        var readable = ReadableContent.Create(
            article.Title,
            article.CleanedText,
            paragraphs,
            article.Author,
            article.PublishedDate);

        page.SetReadableContent(readable);

        _logger.LogDebug(
            "BuildPageFromCachedArticle: url={Url}, title={Title}, paragraphs={Count}",
            url,
            article.Title,
            paragraphs.Count);

        return page;
    }

    /// <summary>
    /// Attempts to get a saved hierarchy config or run AI analysis for the page.
    /// Returns null if no config exists and analysis is not available or not needed.
    /// </summary>
    private async Task<SiteHierarchyConfig?> TryGetOrAnalyzeHierarchyAsync(
        List<LinkInfo> links,
        string pageUrl)
    {
        var configStore = GetHierarchyConfigStore();
        if (configStore == null)
        {
            return null;
        }

        // Check for saved config first
        try
        {
            var existingConfig = await configStore.GetConfigAsync(pageUrl);
            if (existingConfig != null)
            {
                _logger.LogDebug("Using saved hierarchy config for {Url}", pageUrl);
                return existingConfig;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check hierarchy config for {Url}", pageUrl);
        }

        // No saved config — show hint for layout customization.
        // AI analysis is now on-demand only (via L key / layout chooser),
        // not auto-triggered on page load.
        var hintLinkCount = links.Count(l => l.Type == Domain.Enums.Browser.LinkType.Content);
        if (hintLinkCount >= 3)
        {
            _navigationService.SetStatusMessage("Ctrl+L to customize layout");
        }

        return null;
    }

    /// <summary>
    /// Waits for the user to complete a manual login in the browser window.
    /// </summary>
    private async Task<bool> WaitForManualLoginAsync(string url, string domain, CancellationToken cancellationToken)
    {
        _renderer.RenderManualLogin(url, domain);
        if (_browserSession is IBrowserSession browserSession)
        {
            await browserSession.RestoreWindowAsync();
        }

        var timeout = TimeSpan.FromMinutes(3);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(2000, cancellationToken);
            try
            {
                if (_browserSession is IBrowserSession session && session.HasActiveBrowser && session.IsBrowserAvailable)
                {
                    var page = await session.GetOrCreatePageAsync(false);
                    var currentUrl = page.Url;

                    // Login complete when URL no longer contains login/signin paths
                    if (!currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) &&
                        !currentUrl.Contains("/signin", StringComparison.OrdinalIgnoreCase) &&
                        !currentUrl.Contains("/sign-in", StringComparison.OrdinalIgnoreCase) &&
                        !currentUrl.Contains("/auth/", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Manual login completed for {Domain}, URL is now {Url}", domain, currentUrl);

                        // Capture cookies after successful manual login
                        try
                        {
                            var playwrightCookies = await page.Context.CookiesAsync();
                            var storedCookies = playwrightCookies.Select(c =>
                                new Application.Interfaces.StoredCookie(
                                    c.Name,
                                    c.Value,
                                    c.Domain ?? string.Empty,
                                    c.Path ?? string.Empty,
                                    c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).DateTime : null)).ToList();

                            await _cookieManager.SaveCookiesAsync(storedCookies, cancellationToken);
                            _logger.LogInformation(
                                "Captured {Count} cookies after manual login for {Domain}",
                                storedCookies.Count,
                                domain);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to capture cookies after manual login");
                        }

                        return true;
                    }
                }
                else
                {
                    _logger.LogDebug("No active browser for manual login polling, stopping");
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Error polling login status");
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Lazily resolves the article content cache from the DI container.
    /// Returns null if podcast services are not registered.
    /// </summary>
    private IArticleContentCache? ResolveArticleContentCache()
    {
        if (!_articleContentCacheResolved)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                _articleContentCache = scope.ServiceProvider.GetService<IArticleContentCache>();
            }
            catch
            {
                // Podcast services may not be registered
            }

            _articleContentCacheResolved = true;
        }

        return _articleContentCache;
    }

    /// <summary>
    /// Lazily resolves the hierarchy config store from the DI container.
    /// </summary>
    private IHierarchyConfigStore? GetHierarchyConfigStore()
    {
        if (!_hierarchyConfigStoreResolved)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                _hierarchyConfigStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            }
            catch
            {
                // Service may not be registered
            }

            _hierarchyConfigStoreResolved = true;
        }

        return _hierarchyConfigStore;
    }
}
