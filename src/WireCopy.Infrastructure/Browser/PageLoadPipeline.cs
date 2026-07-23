// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Animations;
using WireCopy.Infrastructure.Podcast.Cache;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Encapsulates the page-loading pipeline: article cache lookup, build cache,
/// HTTP/browser fetch, bot challenge handling, content quality retry, paywall
/// retry, and auto-login fallback.
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
    private readonly IThemeProvider _themeProvider;
    private readonly ILogger<PageLoadPipeline> _logger;

    // Lazily resolved article content cache (may not be registered if podcast services are disabled)
    private IArticleContentCache? _articleContentCache;
    private bool _articleContentCacheResolved;

    // Lazily resolved hierarchy services (may not need to construct until first use)
    private IHierarchyConfigStore? _hierarchyConfigStore;
    private bool _hierarchyConfigStoreResolved;

    // Lazily resolved article-layout services (only constructed when an
    // article-shaped page slips past the heuristic content extractor and we
    // need to escalate to the saved-config / AI pipeline).
    private IArticleLayoutStore? _articleLayoutStore;
    private bool _articleLayoutStoreResolved;
    private ISelectorBasedArticleExtractor? _selectorExtractor;
    private bool _selectorExtractorResolved;
    private IAiArticleExtractor? _aiArticleExtractor;
    private bool _aiArticleExtractorResolved;

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
        IThemeProvider themeProvider,
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
        _themeProvider = themeProvider;
        _browserConfig = browserConfig;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full page-loading pipeline for a URL: article cache, build cache,
    /// network fetch, bot challenge, content quality retry, paywall retry, and
    /// auto-login.
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
                    var cachedArticle = await articleCache.TryGetAsync(url, cancellationToken).ConfigureAwait(false);
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
                            await articleCache.RemoveAsync(url, cancellationToken).ConfigureAwait(false);
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

            // workspace-romy.9: reject caches built by older extraction logic.
            // Cached links carry frozen Type values, and a cache hit skips
            // extraction entirely — so without this gate an extraction change
            // (e.g. aggregator story promotion) never reaches revisits. This
            // is how techmeme kept showing zero articles.
            else if (buildCache.ExtractionVersion != LinkExtractor.ExtractionVersion)
            {
                _logger.LogInformation(
                    "LoadAsync: rejecting stale build cache (extraction v{Old} != v{New}): {Url}",
                    buildCache.ExtractionVersion,
                    LinkExtractor.ExtractionVersion,
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
                url, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (inFlightResult != null && inFlightResult.Success)
            {
                _logger.LogInformation("Using in-flight preload result for {Url}", url);
                fetchMethod = FetchMethod.Cached;
                var (inFlightPage, inFlightBuild) = await BuildPageAsync(inFlightResult, url, cancellationToken).ConfigureAwait(false);
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
            new PageLoadRequest { Url = url, ForceBrowser = true },
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "LoadAsync initial load: url={Url}, success={Success}, method={Method}, contentLength={Length}",
            url,
            loadResult.Success,
            loadResult.FetchMethod,
            loadResult.Html?.Length ?? 0);

        if (!loadResult.Success)
        {
            // Surface the typed HITL verdict (CAPTCHA, login, cookie banner, 2FA, paywall,
            // region block) when the loader recognised one. The orchestrator catches this
            // exception and renders a variant-aware reader-view box instead of the generic
            // "Something went wrong" error.
            if (loadResult.RequiredAction != null)
            {
                throw new HumanActionRequiredException(loadResult.RequiredAction, loadResult.ErrorMessage);
            }

            throw new InvalidOperationException($"Failed to load page: {loadResult.ErrorMessage}");
        }

        fetchMethod = loadResult.FetchMethod;

        reportStage?.Invoke("Extracting content...");
        PageBuildCache? lastBuildResult;
        Page page;
        (page, lastBuildResult) = await BuildPageAsync(loadResult, url, cancellationToken).ConfigureAwait(false);

        // Bot challenge handling: if browser returned a challenge page in headed mode,
        // wait for the user to solve it in the visible browser window (interactive, must be synchronous)
        var challengeResult = await HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken).ConfigureAwait(false);
        if (challengeResult != null)
        {
            fetchMethod = challengeResult.FetchMethod;
            loadResult = challengeResult;
            _pageCache.Put(url, challengeResult);
            (page, lastBuildResult) = await BuildPageAsync(challengeResult, url, cancellationToken).ConfigureAwait(false);
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

            reportStage?.Invoke("Retrying with browser...");
            (page, lastBuildResult, fetchMethod) = await RetryLoadAndBuildAsync(
                url,
                new PageLoadRequest { Url = url, ForceRefresh = true },
                page,
                lastBuildResult,
                fetchMethod,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Page loaded: {Title} - {LinkCount} links, {HasReadable} readable",
            page.Metadata.Title,
            page.LinkTree?.TotalLinks ?? 0,
            page.HasReadableContent() ? "has" : "no");

        // Determine if quality improvement retries are needed.
        // If so, return the current page immediately and schedule retries in the background.
        // The user can interact with the (possibly truncated) content while retries run.
        Task<PageLoadPipelineResult>? qualityRetryTask = null;
        var needsQualityRetry = NeedsQualityRetry(page, fetchMethod, browserAvailable);

        if (needsQualityRetry)
        {
            // Cache what we have so far (will be replaced if retry succeeds)
            StoreBuildCache(url, page, lastBuildResult);

            var capturedFetchMethod = fetchMethod;
            qualityRetryTask = Task.Run(() =>
                RunQualityRetriesAsync(url, page, capturedFetchMethod, browserAvailable, cancellationToken),
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

        var links = await _linkExtractor.ExtractLinksAsync(loadResult.Html, loadResult.Url ?? requestedUrl, cancellationToken).ConfigureAwait(false);

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
        var hierarchyConfig = await TryGetOrAnalyzeHierarchyAsync(links, finalUrl).ConfigureAwait(false);

        // workspace-5oe9.5: route on the shared decision. Durable Sections win
        // FIRST and store-agnostically, so an AiCurated config (or one
        // rehydrated from the build cache without Kind/AiResult) re-applies the
        // generalizing pattern path instead of decaying to the stale URL
        // snapshot.
        var route = HierarchyRouteResolver.Decide(hierarchyConfig);
        if (route == HierarchyRoute.PatternConfig)
        {
            tree = await _treeBuilder.BuildTreeAsync(links, hierarchyConfig!, cancellationToken).ConfigureAwait(false);

            // workspace-9k27.1: the saved sections no longer match this page —
            // the builder fell back to document order. Say so instead of
            // claiming an AI-curated layout over what is really a plain tree.
            if (tree.HierarchyConfigStale)
            {
                _navigationService.SetAiHierarchy(false);
                _navigationService.SetStatusMessage("Saved layout no longer matches this page · g l to re-run setup");
            }
            else
            {
                _navigationService.SetAiHierarchy(true);
                if (hierarchyConfig!.NeedsReanalyze)
                {
                    _navigationService.SetStatusMessage("Saved layout is a legacy snapshot · g l to re-run AI setup");
                }
            }
        }
        else if (route == HierarchyRoute.RssFeed)
        {
            // Saved RSS layout: fetch feed and build tree from feed items
            var feedItems = await _feedDetector.ParseFeedAsync(hierarchyConfig!.RssFeedUrl!, cancellationToken).ConfigureAwait(false);
            if (feedItems.Count > 0)
            {
                tree = await _treeBuilder.BuildTreeAsync(feedItems, cancellationToken).ConfigureAwait(false);
                _navigationService.SetAiHierarchy(false);
                _navigationService.SetStatusMessage($"RSS feed · {feedItems.Count} articles");
            }
            else
            {
                // Feed unavailable — fallback to document-order
                tree = await _treeBuilder.BuildTreeAsync(links, cancellationToken).ConfigureAwait(false);
                _navigationService.SetAiHierarchy(false);
                _navigationService.SetStatusMessage("Saved RSS feed unavailable · g l to reconfigure");
            }
        }
        else if (route == HierarchyRoute.AiSnapshot)
        {
            // Legacy per-URL snapshot (no durable Sections): filter ads, reorder
            // by AI ranks. Decays as URLs rotate — flagged for re-analysis.
            tree = await _treeBuilder.BuildFromAiResultAsync(
                links, hierarchyConfig!.AiResult!, cancellationToken).ConfigureAwait(false);
            _navigationService.SetAiHierarchy(true);
            _navigationService.SetStatusMessage(
                ConfigMigration.NeedsReanalysis(hierarchyConfig)
                    ? "AI curated (legacy snapshot) · g l to re-run so it survives revisits"
                    : $"AI curated · {hierarchyConfig.AiResult!.StoryOrderLinkKeys.Count} stories");
        }
        else
        {
            tree = await _treeBuilder.BuildTreeAsync(links, cancellationToken).ConfigureAwait(false);
            _navigationService.SetAiHierarchy(false);
        }

        page.SetLinkTree(tree);

        // Skip content extraction for link list pages -- only the link tree matters.
        ReadableContent? readable = null;
        if (classification != PageClassification.LinkList)
        {
            // workspace-8qyo: a SAVED per-domain layout (user-tuned via 'L' or an
            // AI config that passed its self-test) is AUTHORITATIVE — apply it
            // before generic readability so a tuned site never regresses to the
            // heuristics it was tuned to fix. Misses fall through unchanged.
            readable = await TrySavedSelectorConfigAsync(finalUrl, loadResult.Html).ConfigureAwait(false);

            readable ??= await _contentExtractor.ExtractAsync(loadResult.Html, loadResult.Url ?? requestedUrl, cancellationToken).ConfigureAwait(false);

            // Escalate to the AI article-layout pipeline when the heuristic
            // extractor returns null for a URL that *looks* article-shaped
            // (workspace-xusy). The escalation tries a saved per-domain config
            // first, then asks the AI extractor for a fresh selector set when
            // there's no saved config or the saved config also misses.
            //
            // workspace-l811: also escalate when the heuristic returns content
            // that *fails* the article quality bar — paywall previews and
            // ad-heavy results currently return non-null with 1–2 thin
            // paragraphs. We retry via the AI path; if AI returns nothing
            // useful, we keep the heuristic's low-quality content rather than
            // showing the user a blank reader.
            var needsAiEscalation = !string.IsNullOrEmpty(loadResult.Html)
                && PageClassifier.IsArticleUrlPattern(finalUrl)
                && (readable == null || !ReadableContentExtractor.ValidateContentQuality(readable.Paragraphs));

            if (needsAiEscalation)
            {
                var aiReadable = await TryArticleSelectorEscalationAsync(
                    finalUrl, loadResult.Html, cancellationToken).ConfigureAwait(false);

                // Prefer AI output when it is non-null and meets the quality
                // bar. When AI returns nothing, fall back to whatever the
                // heuristic produced (possibly null).
                if (aiReadable != null && ReadableContentExtractor.ValidateContentQuality(aiReadable.Paragraphs))
                {
                    readable = aiReadable;
                }
            }

            if (readable != null)
            {
                page.SetReadableContent(readable);

                // Populate the persistent article cache so podcast generation can
                // skip re-extraction for any article the user has already loaded.
                // Skip paywalled-domain articles with low word count (likely truncated previews).
                if (!_browserConfig.IsPaywalledDomain(finalUrl) || readable.WordCount >= 200)
                {
                    await TryPopulateArticleCacheAsync(finalUrl, readable, cancellationToken).ConfigureAwait(false);
                }
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
            ExtractionVersion = LinkExtractor.ExtractionVersion,
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

        // workspace-5oe9.5: identical store-agnostic routing as BuildPage.
        // Durable Sections route to the pattern path FIRST — a build-cache
        // rehydrate drops Kind/Strategy/AiResult, so keying on Sections.Count>0
        // is what keeps a configured site curated after a restart.
        //
        // workspace-wylw: the build-cache entry's config is a SNAPSHOT taken at
        // extraction time. A config saved AFTER that (g l wizard, chooser,
        // reconfigure) lives only in the store, so a cache-hit revisit — same
        // session or after a restart — silently rendered document order. The
        // store is the durable source of truth; prefer it whenever it has a
        // config for this URL and fall back to the snapshot otherwise.
        var hierarchyConfig = cache.HierarchyConfig;
        var configStore = GetHierarchyConfigStore();
        if (configStore != null)
        {
            try
            {
                var stored = await configStore.GetConfigAsync(cache.FinalUrl).ConfigureAwait(false);
                if (stored != null && !ReferenceEquals(stored, hierarchyConfig))
                {
                    _logger.LogDebug(
                        "RebuildFromBuildCache: using stored hierarchy config for {Url} (cache snapshot {Snapshot})",
                        cache.FinalUrl,
                        hierarchyConfig == null ? "null" : "stale");
                    hierarchyConfig = stored;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RebuildFromBuildCache: config store lookup failed; using cache snapshot");
            }
        }

        NavigationTree tree;
        var route = HierarchyRouteResolver.Decide(hierarchyConfig);
        if (route == HierarchyRoute.AiSnapshot)
        {
            tree = await _treeBuilder.BuildFromAiResultAsync(cache.Links, hierarchyConfig!.AiResult!).ConfigureAwait(false);
            _navigationService.SetAiHierarchy(true);
        }
        else if (hierarchyConfig != null)
        {
            // PatternConfig (or an RSS/other config in cache): the generalizing
            // hierarchy builder. Build cache cannot replay an RSS fetch, so a
            // cached RSS config renders its grouped links here.
            tree = await _treeBuilder.BuildTreeAsync(cache.Links, hierarchyConfig).ConfigureAwait(false);

            // workspace-9k27.1: same staleness surfacing as the fresh-load path.
            if (tree.HierarchyConfigStale)
            {
                _navigationService.SetAiHierarchy(false);
                _navigationService.SetStatusMessage("Saved layout no longer matches this page · g l to re-run setup");
            }
            else
            {
                _navigationService.SetAiHierarchy(true);
            }
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
        CancellationToken cancellationToken)
    {
        if (loadResult.FetchMethod != FetchMethod.Browser ||
            !PageLoader.IsBotChallengePage(loadResult.Html))
        {
            return null;
        }

        _logger.LogWarning("Bot challenge detected in headed mode, waiting for user to resolve: {Url}", url);

        // Bring browser window to foreground so user can see and solve the challenge
        if (_browserSession is IBrowserSession challengeSession)
        {
            await challengeSession.RestoreWindowAsync().ConfigureAwait(false);
        }

        _renderer.RenderChallenge(url);

        var challengeTimeout = TimeSpan.FromMinutes(2);
        var sw = Stopwatch.StartNew();
        var resolved = false;

        // Design-system spec: breathing bar runs while the app waits on a human
        // (bot-challenge / login). Loops at ~3.6s/cycle in AccentFg until disposed.
        using var breathingBar = BreathingBarAnimation.StartForBotChallenge(
            BuiltInThemes.Get(_themeProvider.CurrentTheme));

        while (sw.Elapsed < challengeTimeout && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            try
            {
                if (_browserSession is IBrowserSession session && session.IsBrowserAvailable)
                {
                    var page = await session.GetOrCreatePageAsync().ConfigureAwait(false);
                    var currentSource = await page.ContentAsync().ConfigureAwait(false);
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

        // Minimize browser after challenge resolved (or timed out). RestoreWindowAsync had
        // brought it forward for the user, so hand keyboard focus back to the terminal
        // (workspace-fihe) instead of stranding it on the now-parked browser.
        if (_browserSession is IBrowserSession resolvedSession)
        {
            await resolvedSession.MinimizeWindowAsync(weJustActivatedBrowser: true).ConfigureAwait(false);
        }

        if (!resolved)
        {
            return null;
        }

        _pageCache.Remove(url);
        var retryResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, ForceRefresh = true },
            cancellationToken).ConfigureAwait(false);

        return retryResult.Success ? retryResult : null;
    }

    /// <summary>
    /// The recurring recovery step shared by the load/quality-retry ladders:
    /// evict <paramref name="url"/> from the cache, re-load it with
    /// <paramref name="request"/>, and — only if that load succeeds — rebuild the
    /// page. On success returns the rebuilt page, its build cache, and the new
    /// fetch method; on failure returns the supplied current values unchanged
    /// (the cache is evicted either way, matching the original inline blocks).
    /// </summary>
    private async Task<(Page Page, PageBuildCache? Build, FetchMethod FetchMethod)> RetryLoadAndBuildAsync(
        string url,
        PageLoadRequest request,
        Page currentPage,
        PageBuildCache? currentBuild,
        FetchMethod currentFetchMethod,
        CancellationToken cancellationToken)
    {
        _pageCache.Remove(url);
        var retryResult = await _pageLoader.LoadAsync(request, cancellationToken).ConfigureAwait(false);
        if (!retryResult.Success)
        {
            return (currentPage, currentBuild, currentFetchMethod);
        }

        var (page, build) = await BuildPageAsync(retryResult, url, cancellationToken).ConfigureAwait(false);
        return (page, build, retryResult.FetchMethod);
    }

    /// <summary>
    /// Determines whether the page needs background quality improvement retries.
    /// </summary>
#pragma warning disable SA1204 // kept adjacent to RunQualityRetriesAsync (the retries it gates) for readability
    private static bool NeedsQualityRetry(
        Page page,
        FetchMethod fetchMethod,
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

        // Empty LinkList — browser may have hit a challenge or JS rendering issue
        if (page.Classification == PageClassification.LinkList &&
            !page.HasLinks())
        {
            return true;
        }

        return false;
    }
#pragma warning restore SA1204

    /// <summary>
    /// Runs quality improvement retries in the background. Returns an improved result
    /// if any retry produces better content, otherwise returns the original page.
    /// </summary>
    private async Task<PageLoadPipelineResult> RunQualityRetriesAsync(
        string url,
        Page originalPage,
        FetchMethod fetchMethod,
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

                        if (await autoLogin.HasCredentialsAsync(domain, cancellationToken).ConfigureAwait(false))
                        {
                            _logger.LogInformation(
                                "Quality retry: auto-login for {Domain}: {Url}", domain, url);

                            var loginResult = await autoLogin.LoginAsync(domain, cancellationToken).ConfigureAwait(false);
                            if (loginResult.Success || loginResult.ManualLoginRequired)
                            {
                                if (loginResult.ManualLoginRequired)
                                {
                                    var manualLoginOk = await WaitForManualLoginAsync(url, domain, cancellationToken).ConfigureAwait(false);
                                    if (!manualLoginOk)
                                    {
                                        _logger.LogInformation("Manual login was not completed for {Domain}", domain);
                                    }
                                }

                                (page, lastBuildResult, fetchMethod) = await RetryLoadAndBuildAsync(
                                    url,
                                    new PageLoadRequest { Url = url, ForceRefresh = true },
                                    page,
                                    lastBuildResult,
                                    fetchMethod,
                                    cancellationToken).ConfigureAwait(false);
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

                (page, lastBuildResult, fetchMethod) = await RetryLoadAndBuildAsync(
                    url,
                    new PageLoadRequest { Url = url, ForceRefresh = true, ForceBrowser = true },
                    page,
                    lastBuildResult,
                    fetchMethod,
                    cancellationToken).ConfigureAwait(false);
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
            var existingConfig = await configStore.GetConfigAsync(pageUrl).ConfigureAwait(false);
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
            _navigationService.SetStatusMessage("g l to customize layout");
        }

        return null;
    }

    /// <summary>
    /// Waits for the user to complete a manual login in the browser window.
    /// </summary>
    private async Task<bool> WaitForManualLoginAsync(string url, string domain, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(3);

        _renderer.RenderManualLogin(url, domain, elapsedMs: 0, timeoutMs: (long)timeout.TotalMilliseconds);
        if (_browserSession is IBrowserSession browserSession)
        {
            await browserSession.RestoreWindowAsync().ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

            // workspace-mokw: re-render each poll tick so the spinner moves and
            // the elapsed/timeout clock counts up — a silent 3-minute wait is
            // indistinguishable from a hang.
            _renderer.RenderManualLogin(
                url,
                domain,
                sw.ElapsedMilliseconds,
                (long)timeout.TotalMilliseconds);

            try
            {
                if (_browserSession is IBrowserSession session && session.HasActiveBrowser && session.IsBrowserAvailable)
                {
                    var page = await session.GetOrCreatePageAsync().ConfigureAwait(false);
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
                            var playwrightCookies = await page.Context.CookiesAsync().ConfigureAwait(false);
                            var storedCookies = playwrightCookies.Select(c =>
                                new Application.Interfaces.StoredCookie(
                                    c.Name,
                                    c.Value,
                                    c.Domain ?? string.Empty,
                                    c.Path ?? string.Empty,
                                    c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).DateTime : null)).ToList();

                            await _cookieManager.SaveCookiesAsync(storedCookies, cancellationToken).ConfigureAwait(false);
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
            // GetService returns null for unregistered services; the only realistic
            // exception path here is scope-creation failure, which would be a deeper
            // host problem we shouldn't silently swallow.
            using var scope = _scopeFactory.CreateScope();
            _articleContentCache = scope.ServiceProvider.GetService<IArticleContentCache>();
            _articleContentCacheResolved = true;
        }

        return _articleContentCache;
    }

    private IArticleLayoutStore? ResolveArticleLayoutStore()
    {
        if (!_articleLayoutStoreResolved)
        {
            using var scope = _scopeFactory.CreateScope();
            _articleLayoutStore = scope.ServiceProvider.GetService<IArticleLayoutStore>();
            _articleLayoutStoreResolved = true;
        }

        return _articleLayoutStore;
    }

    private ISelectorBasedArticleExtractor? ResolveSelectorExtractor()
    {
        if (!_selectorExtractorResolved)
        {
            using var scope = _scopeFactory.CreateScope();
            _selectorExtractor = scope.ServiceProvider.GetService<ISelectorBasedArticleExtractor>();
            _selectorExtractorResolved = true;
        }

        return _selectorExtractor;
    }

    private IAiArticleExtractor? ResolveAiArticleExtractor()
    {
        if (!_aiArticleExtractorResolved)
        {
            using var scope = _scopeFactory.CreateScope();
            _aiArticleExtractor = scope.ServiceProvider.GetService<IAiArticleExtractor>();
            _aiArticleExtractorResolved = true;
        }

        return _aiArticleExtractor;
    }

    /// <summary>
    /// Escalation chain when the heuristic <see cref="IReadableContentExtractor"/>
    /// returns null (or fails the quality gate) for an article-shaped URL. Tries:
    /// <list type="number">
    ///   <item>Saved per-domain article-layout config (if present)</item>
    ///   <item>AI extractor — only when (1) miss AND saved config doesn't already produce non-empty content</item>
    /// </list>
    /// On AI success the candidate config is self-tested via the
    /// <see cref="ISelectorBasedArticleExtractor"/>; only configs that produce
    /// non-empty content meeting the global quality gate are persisted.
    /// </summary>
    /// <returns>Extracted content or null if the chain produced nothing.</returns>
    /// <summary>
    /// Applies the saved per-domain selector config when one exists
    /// (workspace-8qyo). Null when there is no config, no entry matches, or the
    /// extraction misses its quality bar — callers fall through to generic
    /// readability.
    /// </summary>
    private async Task<ReadableContent?> TrySavedSelectorConfigAsync(string url, string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var store = ResolveArticleLayoutStore();
        var selectorExtractor = ResolveSelectorExtractor();
        var domain = ArticleLayoutDomains.FromUrl(url);
        if (store is null || selectorExtractor is null || domain is null)
        {
            return null;
        }

        var savedConfig = await store.LoadAsync(domain).ConfigureAwait(false);
        if (savedConfig == null)
        {
            return null;
        }

        var extracted = selectorExtractor.Extract(savedConfig, url, html);
        if (extracted != null)
        {
            _logger.LogInformation(
                "Article extracted via saved selector config for {Url} ({Words} words)",
                url,
                extracted.WordCount);
        }

        return extracted;
    }

    private async Task<ReadableContent?> TryArticleSelectorEscalationAsync(
        string url,
        string html,
        CancellationToken cancellationToken)
    {
        if (!PageClassifier.IsArticleUrlPattern(url))
        {
            return null;
        }

        var store = ResolveArticleLayoutStore();
        var selectorExtractor = ResolveSelectorExtractor();
        if (store is null || selectorExtractor is null)
        {
            return null;
        }

        var domain = ArticleLayoutDomains.FromUrl(url);
        if (domain is null)
        {
            return null;
        }

        // 1. Saved config first.
        var savedConfig = await store.LoadAsync(domain).ConfigureAwait(false);
        if (savedConfig != null)
        {
            var fromSaved = selectorExtractor.Extract(savedConfig, url, html);
            if (fromSaved != null)
            {
                _logger.LogInformation(
                    "Article extracted via saved selector config for {Url} ({Words} words)",
                    url,
                    fromSaved.WordCount);
                return fromSaved;
            }
        }

        // 2. AI escalation.
        var aiExtractor = ResolveAiArticleExtractor();
        if (aiExtractor is null || !aiExtractor.IsConfigured)
        {
            _logger.LogDebug("AI article extractor unavailable for {Url}; skipping", url);
            return null;
        }

        ArticleSelectorConfig? candidate;
        try
        {
            // workspace-wef6.5: surface the 30-60s analysis in the animated
            // activity slot — it was previously invisible.
            _navigationService.SetActivity("ai", "✦ analyzing layout…", priority: 1);
            candidate = await aiExtractor.AnalyzeAsync(url, html, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI article extractor failed for {Url}", url);
            return null;
        }
        finally
        {
            _navigationService.ClearActivity("ai");
        }

        if (candidate == null || candidate.PageTypes.Count == 0)
        {
            return null;
        }

        // Self-test gate: run the candidate against the live HTML.
        var fromAi = selectorExtractor.Extract(candidate, url, html);
        if (fromAi == null)
        {
            _logger.LogInformation(
                "AI article extractor produced a candidate for {Url} that failed the self-test gate; not persisting",
                url);
            return null;
        }

        // Merge with any pre-existing saved config (replace the entry that
        // shares the same Name; otherwise append).
        var merged = MergeArticleConfigs(savedConfig, candidate);
        try
        {
            await store.SaveAsync(merged).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist AI-generated article layout for {Domain}", merged.Domain);
        }

        _logger.LogInformation(
            "AI extracted article for {Url} ({Words} words); config persisted",
            url,
            fromAi.WordCount);
        return fromAi;
    }

#pragma warning disable SA1204 // grouped with the escalation logic that produces it
    private static ArticleSelectorConfig MergeArticleConfigs(
        ArticleSelectorConfig? existing,
        ArticleSelectorConfig fresh) =>
        ArticleConfigMerger.Merge(existing, fresh);
#pragma warning restore SA1204

    /// <summary>
    /// Stores extracted readable content in the persistent article cache so podcast
    /// generation can skip re-extraction. No-op when the article cache service
    /// is not registered or the put fails (cache is best-effort).
    /// </summary>
    private async Task TryPopulateArticleCacheAsync(
        string url,
        ReadableContent content,
        CancellationToken cancellationToken)
    {
        var articleCache = ResolveArticleContentCache();
        if (articleCache is null)
        {
            return;
        }

        try
        {
            var article = new Podcast.ExtractedArticle
            {
                Title = content.Title,
                CleanedText = content.CleanedText,
                Author = content.Author,
                Url = url,
                WordCount = content.WordCount,
                PublishedDate = content.PublishedDate,
            };

            await articleCache.PutAsync(url, article, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Pre-populated article cache for {Url} ({Words} words)", url, content.WordCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to populate article cache for {Url}", url);
        }
    }

    /// <summary>
    /// Lazily resolves the hierarchy config store from the DI container.
    /// </summary>
    private IHierarchyConfigStore? GetHierarchyConfigStore()
    {
        if (!_hierarchyConfigStoreResolved)
        {
            using var scope = _scopeFactory.CreateScope();
            _hierarchyConfigStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            _hierarchyConfigStoreResolved = true;
        }

        return _hierarchyConfigStore;
    }
}
