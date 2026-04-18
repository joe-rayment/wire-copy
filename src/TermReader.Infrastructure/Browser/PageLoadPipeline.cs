// Educational and personal use only.

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
    private IHierarchyAnalyzer? _hierarchyAnalyzer;
    private bool _hierarchyAnalyzerResolved;

    public PageLoadPipeline(
        IPageLoader pageLoader,
        ILinkExtractor linkExtractor,
        INavigationTreeBuilder treeBuilder,
        IReadableContentExtractor contentExtractor,
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
            _logger.LogInformation("LoadAsync: build cache HIT for {Url}, skipping extraction", url);

            // Refresh TTL so link-list pages stay cached across revisits
            if (buildCache.Classification == PageClassification.LinkList)
            {
                _pageCache.ApplyLinkListTtl(url);
            }

            return new PageLoadPipelineResult
            {
                Page = RebuildFromBuildCache(buildCache),
                FetchMethod = FetchMethod.Cached,
                BuildResult = null,
            };
        }

        var htmlCached = _pageCache.Contains(url);
        _logger.LogInformation("LoadAsync: HTML cache {Result} for {Url}", htmlCached ? "HIT" : "MISS", url);
        if (!htmlCached)
        {
            _renderer.RenderLoading(url);

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

        // For paywalled domains with cookies, use the background browser (with auth cookies)
        // so articles load fully.
        var hasBuildCache = _pageCache.TryGetBuildCache(url) != null;
        if (hasBuildCache)
        {
            _logger.LogInformation("Skipping ForceBrowser -- build cache exists for {Url}", url);
        }

        var forceBrowser = false;
        if (!hasBuildCache && _browserConfig.IsPaywalledDomain(url))
        {
            var cookies = await _cookieManager.LoadCookiesAsync();
            var host = new Uri(url).Host;
            var hasDomainCookies = cookies.Any(c =>
            {
                var d = c.Domain.TrimStart('.');
                return host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase);
            });

            if (hasDomainCookies)
            {
                _logger.LogInformation("Paywalled domain with cookies, using browser for cache miss: {Url}", url);
                forceBrowser = true;
            }
        }

        reportStage?.Invoke(forceBrowser ? "Loading via browser..." : "Fetching page...");

        var loadResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceBrowser = forceBrowser },
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
        // wait for the user to solve it in the visible browser window
        var challengeResult = await HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken);
        if (challengeResult != null)
        {
            fetchMethod = challengeResult.FetchMethod;
            loadResult = challengeResult;
            _pageCache.Put(url, challengeResult);
            (page, lastBuildResult) = await BuildPageAsync(challengeResult, url, cancellationToken);
        }

        // Content-quality fallback: if no content from HTTP/cached page, retry with browser.
        var browserAvailable = (_browserSession as IBrowserSession)?.IsBrowserAvailable ?? false;
        var hasLinks = page.LinkTree != null && page.LinkTree.TotalLinks > 0;

        if (!page.HasReadableContent() && !hasLinks && loadResult.FetchMethod != FetchMethod.Browser && browserAvailable)
        {
            _logger.LogInformation(
                "No readable content from {FetchMethod} page, retrying with browser: {Url}",
                loadResult.FetchMethod,
                url);

            _pageCache.Remove(url);
            _renderer.RenderLoading(url);

            var retryResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true },
                cancellationToken);

            if (retryResult.Success)
            {
                fetchMethod = retryResult.FetchMethod;
                (page, lastBuildResult) = await BuildPageAsync(retryResult, url, cancellationToken);
            }
        }

        // Paywalled domain content-quality fallback: if HTTP returned truncated content
        // from a paywalled domain, either retry with authenticated browser (cookies exist)
        // or prompt user to log in (no cookies -- silent retry would show same paywall).
        // Skip for LinkList pages -- section pages aren't paywalled.
        if (browserAvailable &&
            page.Classification != PageClassification.LinkList &&
            page.HasReadableContent() &&
            fetchMethod != FetchMethod.Browser &&
            _browserConfig.IsPaywalledDomain(url) &&
            page.ReadableContent!.WordCount < 500)
        {
            var cookieInfo = await _cookieManager.GetCookieInfoAsync();
            var hasCookies = cookieInfo is { Exists: true, IsExpired: false };

            if (hasCookies)
            {
                // Have cookies -- retry with browser using authenticated session
                _logger.LogInformation(
                    "Paywalled domain with truncated content ({Words} words), retrying with authenticated browser: {Url}",
                    page.ReadableContent.WordCount,
                    url);

                _pageCache.Remove(url);
                _renderer.RenderLoading(url);

                var truncRetryResult = await _pageLoader.LoadAsync(
                    new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true, ForceBrowser = true },
                    cancellationToken);

                if (truncRetryResult.Success)
                {
                    fetchMethod = truncRetryResult.FetchMethod;
                    (page, lastBuildResult) = await BuildPageAsync(truncRetryResult, url, cancellationToken);
                }
            }
            else
            {
                // No cookies -- don't retry (browser would show same paywall).
                // Show preview content with a clear prompt to use Shift+I.
                _logger.LogInformation(
                    "Paywalled domain with truncated content ({Words} words) and no cookies, prompting Shift+I: {Url}",
                    page.ReadableContent.WordCount,
                    url);

                _navigationService.SetStatusMessage(
                    "Shift+I to log in for full content",
                    TimeSpan.FromMinutes(5));
            }
        }

        // Paywall fallback: if content is paywalled and was fetched via HTTP, retry with
        // browser (which has cookie support) if cookies are available
        if (browserAvailable && page.ReadableContent?.IsPaywalled == true && fetchMethod != FetchMethod.Browser)
        {
            var cookieInfo = await _cookieManager.GetCookieInfoAsync();
            if (cookieInfo is { Exists: true, IsExpired: false })
            {
                _logger.LogInformation(
                    "Paywall detected, retrying with authenticated session: {Url}", url);

                _pageCache.Remove(url);
                _renderer.RenderLoading(url);

                var paywallRetryResult = await _pageLoader.LoadAsync(
                    new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true, ForceBrowser = true },
                    cancellationToken);

                if (paywallRetryResult.Success)
                {
                    fetchMethod = paywallRetryResult.FetchMethod;
                    (page, lastBuildResult) = await BuildPageAsync(paywallRetryResult, url, cancellationToken);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Paywall detected but no valid cookies available: {Url}", url);
            }
        }

        // Auto-login fallback: if content is still paywalled, attempt auto-login using stored credentials
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
                            "Paywall detected with stored credentials, attempting auto-login: {Url}", url);

                        var loginResult = await autoLogin.LoginAsync(domain, cancellationToken);
                        if (loginResult.Success || loginResult.ManualLoginRequired)
                        {
                            if (loginResult.ManualLoginRequired)
                            {
                                _logger.LogInformation(
                                    "Manual login required for {Domain}, waiting for user: {Reason}",
                                    domain,
                                    loginResult.ErrorMessage);
                                var manualLoginOk = await WaitForManualLoginAsync(url, domain, cancellationToken);
                                if (!manualLoginOk)
                                {
                                    _logger.LogInformation("Manual login was not completed for {Domain}", domain);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("Auto-login succeeded for {Domain}, retrying page load", domain);
                            }

                            _pageCache.Remove(url);
                            _renderer.RenderLoading(url);

                            var autoLoginRetryResult = await _pageLoader.LoadAsync(
                                new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true },
                                cancellationToken);

                            if (autoLoginRetryResult.Success)
                            {
                                fetchMethod = autoLoginRetryResult.FetchMethod;
                                (page, lastBuildResult) = await BuildPageAsync(autoLoginRetryResult, url, cancellationToken);
                            }
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Auto-login failed for {Domain}: {Error}",
                                domain,
                                loginResult.ErrorMessage);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-login attempt failed for {Url}", url);
            }
        }

        // Article content retry: if page is classified as Article but has no readable content
        // after browser fetch, the JS app may need more time to render.
        if (!page.HasReadableContent() &&
            page.Classification == PageClassification.Article &&
            fetchMethod == FetchMethod.Browser &&
            browserAvailable)
        {
            _logger.LogInformation(
                "Article page with no readable content after browser fetch, retrying with extended wait: {Url}",
                url);

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

        // Headless challenge fallback: if headless browser got a bot challenge,
        // retry in headed mode where DataDome is less likely to block.
        if (!page.HasReadableContent() &&
            page.Classification != PageClassification.LinkList &&
            loadResult.FetchMethod == FetchMethod.Browser &&
            _browserConfig.Headless &&
            PageLoader.IsBotChallengePage(loadResult.Html ?? string.Empty))
        {
            _logger.LogWarning(
                "Bot challenge detected in headless mode, retrying headed: {Url}",
                url);

            _pageCache.Remove(url);
            _renderer.RenderLoading(url);

            var headedResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = false, ForceRefresh = true },
                cancellationToken);

            if (headedResult.Success)
            {
                fetchMethod = headedResult.FetchMethod;
                (page, lastBuildResult) = await BuildPageAsync(headedResult, url, cancellationToken);
            }
        }

        // If content is still paywalled after all fallback attempts, guide the user
        if (page.ReadableContent?.IsPaywalled == true)
        {
            var host = new Uri(url).Host;
            var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? host[4..] : host;
            _navigationService.SetStatusMessage(
                $"Paywall detected on {domain}. Use :cred add {domain} to store credentials, or Shift+I to log in manually.");
        }

        _logger.LogInformation("Page loaded: {Title} - {LinkCount} links, {HasReadable} readable",
            page.Metadata.Title,
            page.LinkTree?.TotalLinks ?? 0,
            page.HasReadableContent() ? "has" : "no");

        // Cache build results so repeat visits skip extraction entirely.
        // Only cache after all fallbacks complete to avoid storing intermediate/truncated results.
        StoreBuildCache(url, page, lastBuildResult);

        return new PageLoadPipelineResult
        {
            Page = page,
            FetchMethod = fetchMethod,
            BuildResult = lastBuildResult,
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

        // Classify the page (Article vs LinkList) using existing extraction signals
        var isArticlePage = ReadableContentExtractor.IsArticlePage(loadResult.Html);
        var articleContainerCount = CountArticleContainers(loadResult.Html);
        var classification = PageClassifier.Classify(links, isArticlePage, articleContainerCount, finalUrl);
        page.SetClassification(classification);
        var contentLinkCount = links.Count(l => l.Type == Domain.Enums.Browser.LinkType.Content);
        _logger.LogInformation(
            "Page classified as {Classification}: {Url} (contentLinks={ContentLinks}, articleContainers={ArticleContainers}, isArticle={IsArticle})",
            classification,
            finalUrl,
            contentLinkCount,
            articleContainerCount,
            isArticlePage);

        // Try AI-powered hierarchy: check saved config first, then analyze if configured
        NavigationTree tree;
        var hierarchyConfig = await TryGetOrAnalyzeHierarchyAsync(links, finalUrl, cancellationToken);
        if (hierarchyConfig != null)
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

        // Capture build inputs for StoreBuildCache
        var buildResult = new PageBuildCache
        {
            Links = links,
            HierarchyConfig = hierarchyConfig,
            ReadableContent = readable,
            Metadata = metadata,
            FinalUrl = finalUrl,
            Classification = classification,
        };

        return (page, buildResult);
    }

    /// <summary>
    /// Rebuilds a Page from cached build results (extracted links, hierarchy, content).
    /// Creates a fresh NavigationTree with clean selection state.
    /// Used by NavigateToAsync for build cache hits.
    /// </summary>
    public Page RebuildFromBuildCache(PageBuildCache cache)
    {
        var page = Page.Create(cache.FinalUrl, string.Empty, cache.Metadata);

        NavigationTree tree;
        if (cache.HierarchyConfig != null)
        {
            tree = _treeBuilder.BuildTreeAsync(cache.Links, cache.HierarchyConfig).GetAwaiter().GetResult();
            _navigationService.SetAiHierarchy(true);
        }
        else
        {
            tree = _treeBuilder.BuildTreeAsync(cache.Links).GetAwaiter().GetResult();
            _navigationService.SetAiHierarchy(false);
        }

        page.SetLinkTree(tree);
        page.SetClassification(cache.Classification);

        if (cache.ReadableContent != null)
        {
            page.SetReadableContent(cache.ReadableContent);
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

        _renderer.RenderLoading(url);
        _pageCache.Remove(url);
        var retryResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, Headless = headless, ForceRefresh = true },
            cancellationToken);

        return retryResult.Success ? retryResult : null;
    }

    /// <summary>
    /// Returns the number of &lt;article&gt; elements in the HTML.
    /// </summary>
    private static int CountArticleContainers(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return 0;
        }

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.SelectNodes("//article")?.Count ?? 0;
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
        string pageUrl,
        CancellationToken cancellationToken)
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

        // No saved config -- try AI analysis if configured
        var analyzer = GetHierarchyAnalyzer();
        if (analyzer == null || !analyzer.IsConfigured)
        {
            // Hint about AI layout on pages with enough content links
            var hintLinkCount = links.Count(l => l.Type == Domain.Enums.Browser.LinkType.Content);
            if (hintLinkCount >= 5)
            {
                _navigationService.SetStatusMessage("AI layout available \u00b7 :set anthropic-key");
            }

            return null;
        }

        // Only analyze if we have content links worth analyzing
        var contentLinkCount = links.Count(l => l.Type == Domain.Enums.Browser.LinkType.Content);
        if (contentLinkCount < 3)
        {
            _logger.LogDebug("Skipping AI analysis for {Url}: only {Count} content links", pageUrl, contentLinkCount);
            return null;
        }

        // Capture screenshot for AI analysis
        var browserSession = _browserSession as IBrowserSession;
        var screenshot = browserSession != null ? await browserSession.CaptureScreenshotAsync() : null;
        if (screenshot == null || screenshot.Length == 0)
        {
            _logger.LogDebug("No screenshot available for AI analysis of {Url}", pageUrl);
            return null;
        }

        try
        {
            _logger.LogInformation("Running AI hierarchy analysis for {Url}", pageUrl);
            _renderer.RenderLoading(pageUrl, "Analyzing layout...");

            var config = await analyzer.AnalyzePageHierarchyAsync(screenshot, links, pageUrl, cancellationToken);

            // Save for future use
            await configStore.SaveConfigAsync(config);
            _logger.LogInformation("Saved AI hierarchy config for {Url} ({SectionCount} sections)", pageUrl, config.Sections.Count);

            _navigationService.SetStatusMessage($"AI layout \u00b7 {config.Sections.Count} sections");

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI hierarchy analysis failed for {Url}, falling back to heuristic", pageUrl);
            return null;
        }
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

    /// <summary>
    /// Lazily resolves the hierarchy analyzer from the DI container.
    /// </summary>
    private IHierarchyAnalyzer? GetHierarchyAnalyzer()
    {
        if (!_hierarchyAnalyzerResolved)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                _hierarchyAnalyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();
            }
            catch
            {
                // Service may not be registered
            }

            _hierarchyAnalyzerResolved = true;
        }

        return _hierarchyAnalyzer;
    }
}
