// Educational and personal use only.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using TermReader.Infrastructure.Podcast.Cache;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Orchestrates the terminal browser functionality.
/// Coordinates page loading, navigation, rendering, and input handling.
/// </summary>
public class BrowserOrchestrator : IBrowserService
{
    private const int MinContentWidth = 40;
    private const int MaxContentWidth = 120;

    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly IInputHandler _inputHandler;
    private readonly NavigationService _navigationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Configuration.BrowserConfiguration _browserConfig;
    private readonly IBrowserSessionControl _browserSession;
    private readonly IResizeDetector _resizeDetector;
    private readonly IPageCache _pageCache;
    private readonly IPreloadService _preloadService;
    private readonly IIdleDetector _idleDetector;
    private readonly ICookieManager _cookieManager;
    private readonly IHttpCookieRefresher _httpCookieRefresher;
    private readonly ILogger<BrowserOrchestrator> _logger;
    private readonly LineCacheManager _lineCacheManager;

    // Tracks FetchMethod from the last LoadPageAsync call for NavigateToAsync
    private FetchMethod _lastLoadFetchMethod;

    // Captures the last build result from BuildPageFromLoadResultAsync for StoreBuildCache
    private PageBuildCache? _lastBuildResult;

    // Background page load state for non-blocking navigation (Phase 2)
    private Task<Page>? _backgroundPageLoad;
    private string? _backgroundLoadUrl;
    private CancellationTokenSource? _backgroundLoadCts;
    private RenderOptions? _backgroundLoadOptions;
    private volatile LoadingStatus? _loadingStatus;

    // Lazily resolved TTS service for checking IsConfigured state
    private ITtsService? _ttsService;
    private bool _ttsServiceResolved;

    // Lazily resolved article content cache (may not be registered if podcast services are disabled)
    private IArticleContentCache? _articleContentCache;
    private bool _articleContentCacheResolved;

    // Lazily resolved hierarchy services (may not need to construct until first use)
    private IHierarchyConfigStore? _hierarchyConfigStore;
    private bool _hierarchyConfigStoreResolved;
    private IHierarchyAnalyzer? _hierarchyAnalyzer;
    private bool _hierarchyAnalyzerResolved;

    // Live preload progress indicator: set by background thread, read by main loop
    private volatile bool _progressDirty;
    private DateTime _lastProgressRender = DateTime.MinValue;

    // Shared context for command handlers (single source of truth for mutable state)
    private readonly CommandContext _commandContext;

    public BrowserOrchestrator(
        IPageLoader pageLoader,
        ILinkExtractor linkExtractor,
        INavigationTreeBuilder treeBuilder,
        IReadableContentExtractor contentExtractor,
        IPageRenderer renderer,
        IInputHandler inputHandler,
        NavigationService navigationService,
        IServiceScopeFactory scopeFactory,
        IBrowserSessionControl browserSession,
        IThemeProvider themeProvider,
        IResizeDetector resizeDetector,
        IPageCache pageCache,
        IPreloadService preloadService,
        IIdleDetector idleDetector,
        ICookieManager cookieManager,
        IHttpCookieRefresher httpCookieRefresher,
        IOptions<Configuration.BrowserConfiguration> browserConfig,
        ILogger<BrowserOrchestrator> logger)
    {
        _pageLoader = pageLoader;
        _linkExtractor = linkExtractor;
        _treeBuilder = treeBuilder;
        _contentExtractor = contentExtractor;
        _browserConfig = browserConfig.Value;
        _renderer = renderer;
        _inputHandler = inputHandler;
        _navigationService = navigationService;
        _scopeFactory = scopeFactory;
        _browserSession = browserSession;
        _resizeDetector = resizeDetector;
        _pageCache = pageCache;
        _preloadService = preloadService;
        _idleDetector = idleDetector;
        _cookieManager = cookieManager;
        _httpCookieRefresher = httpCookieRefresher;
        _logger = logger;
        _lineCacheManager = new LineCacheManager(navigationService, themeProvider);

        // Subscribe to preload progress changes for live status bar updates
        _preloadService.ProgressChanged += OnPreloadProgressChanged;

        _commandContext = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = _renderer,
            InputHandler = _inputHandler,
            ScopeFactory = _scopeFactory,
            Logger = _logger,
            PageCache = _pageCache,
            LineCacheManager = _lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = _preloadService,
            NavigateToAsync = NavigateToAsync,
            ForceRefreshAsync = ForceRefreshAsync,
            InteractiveRefreshAsync = InteractiveRefreshAsync,
            RenderCurrentPageAsync = RenderCurrentPageAsync,
            RefreshCollectionsAsync = RefreshCollectionsAsync,
            RefreshBookmarksAsync = RefreshBookmarksAsync,
            GetCurrentRenderOptions = GetCurrentRenderOptions,
            CreateCollectionService = CreateCollectionService,
            GetReaderViewportHeight = GetReaderViewportHeight,
            GetHierarchicalViewportHeight = GetHierarchicalViewportHeight,
            AdjustScrollForSelection = AdjustScrollForSelection,
            ScrollToSearchMatch = ScrollToSearchMatch,
        };
    }

    public async Task<Page> LoadPageAsync(string url, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading page: {Url}", url);
        _logger.LogDebug("BrowserConfig.Headless = {Headless}", _browserConfig.Headless);

        var loadTimer = System.Diagnostics.Stopwatch.StartNew();
        void ReportStage(string stage) => _loadingStatus = new LoadingStatus
        {
            Stage = stage,
            ElapsedMs = loadTimer.ElapsedMilliseconds,
            Url = url,
        };

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
                        if (IsPaywalledDomain(url) && cachedArticle.WordCount < 200)
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

                            _lastLoadFetchMethod = FetchMethod.Cached;
                            return BuildPageFromCachedArticle(cachedArticle, url);
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
        _logger.LogInformation("LoadPageAsync: checking build cache for {Url}", url);
        var buildCache = _pageCache.TryGetBuildCache(url);
        if (buildCache != null)
        {
            _logger.LogInformation("LoadPageAsync: build cache HIT for {Url}, skipping extraction", url);
            _lastLoadFetchMethod = FetchMethod.Cached;

            // Refresh TTL so link-list pages stay cached across revisits
            if (buildCache.Classification == PageClassification.LinkList)
            {
                _pageCache.ApplyLinkListTtl(url);
            }

            return RebuildPageFromBuildCache(buildCache);
        }

        var htmlCached = _pageCache.Contains(url);
        _logger.LogInformation("LoadPageAsync: HTML cache {Result} for {Url}", htmlCached ? "HIT" : "MISS", url);
        if (!htmlCached)
        {
            _renderer.RenderLoading(url);

            // Note: Esc cancellation is handled by NavigateToAsync — cache-miss loads run
            // on a background thread with a CancellationTokenSource. Esc maps to GoBack,
            // which cancels the CTS and pops the skeleton page.

            // Check if preload service has an in-flight fetch for this URL
            var inFlightResult = await _preloadService.WaitForInFlightAsync(
                url, TimeSpan.FromSeconds(3), cancellationToken);
            if (inFlightResult != null && inFlightResult.Success)
            {
                _logger.LogInformation("Using in-flight preload result for {Url}", url);
                _lastLoadFetchMethod = FetchMethod.Cached;
                var inFlightPage = await BuildPageFromLoadResultAsync(inFlightResult, url, cancellationToken);
                if (inFlightPage.HasReadableContent())
                {
                    return inFlightPage;
                }

                _logger.LogDebug("In-flight preload result had no readable content, proceeding with normal load");
            }
        }

        // For paywalled domains with cookies, use the background browser (with auth cookies)
        // so articles load fully. The browser is pre-warmed after the first paywalled domain
        // visit, so subsequent loads are fast. Cache is checked first (CachingPageLoader) —
        // ForceBrowser only affects cache-miss behavior (browser instead of HTTP).
        var forceBrowser = false;
        if (IsPaywalledDomain(url))
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

        ReportStage(forceBrowser ? "Loading via browser..." : "Fetching page...");

        var loadResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceBrowser = forceBrowser },
            cancellationToken);

        _logger.LogDebug(
            "LoadPageAsync initial load: url={Url}, success={Success}, method={Method}, contentLength={Length}",
            url,
            loadResult.Success,
            loadResult.FetchMethod,
            loadResult.Html?.Length ?? 0);

        if (!loadResult.Success)
        {
            throw new InvalidOperationException($"Failed to load page: {loadResult.ErrorMessage}");
        }

        _lastLoadFetchMethod = loadResult.FetchMethod;

        ReportStage("Extracting content...");
        var page = await BuildPageFromLoadResultAsync(loadResult, url, cancellationToken);

        // Bot challenge handling: if browser returned a challenge page in headed mode,
        // wait for the user to solve it in the visible browser window
        var challengeResult = await HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken);
        if (challengeResult != null)
        {
            _lastLoadFetchMethod = challengeResult.FetchMethod;
            page = await BuildPageFromLoadResultAsync(challengeResult, url, cancellationToken);
        }

        // Content-quality fallback: if no content from HTTP/cached page, retry with browser.
        // Skip when browser is unavailable — the retry would just repeat the same HTTP fetch.
        // Skip when the page has links — section/index pages (e.g., NYT front page) have links
        // but no "readable" article content, which is expected, not a quality issue.
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
                _lastLoadFetchMethod = retryResult.FetchMethod;
                page = await BuildPageFromLoadResultAsync(retryResult, url, cancellationToken);
            }
        }

        // Paywalled domain content-quality fallback: if HTTP returned truncated content
        // from a paywalled domain, either retry with authenticated browser (cookies exist)
        // or prompt user to log in (no cookies — silent retry would show same paywall).
        // Skip for LinkList pages — section pages aren't paywalled.
        if (browserAvailable &&
            page.Classification != PageClassification.LinkList &&
            page.HasReadableContent() &&
            _lastLoadFetchMethod != FetchMethod.Browser &&
            IsPaywalledDomain(url) &&
            page.ReadableContent!.WordCount < 500)
        {
            var cookieInfo = await _cookieManager.GetCookieInfoAsync();
            var hasCookies = cookieInfo is { Exists: true, IsExpired: false };

            if (hasCookies)
            {
                // Have cookies — retry with browser using authenticated session
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
                    _lastLoadFetchMethod = truncRetryResult.FetchMethod;
                    page = await BuildPageFromLoadResultAsync(truncRetryResult, url, cancellationToken);
                }
            }
            else
            {
                // No cookies — don't retry (browser would show same paywall).
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
        if (browserAvailable && page.ReadableContent?.IsPaywalled == true && _lastLoadFetchMethod != FetchMethod.Browser)
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
                    _lastLoadFetchMethod = paywallRetryResult.FetchMethod;
                    page = await BuildPageFromLoadResultAsync(paywallRetryResult, url, cancellationToken);
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
                                _lastLoadFetchMethod = autoLoginRetryResult.FetchMethod;
                                page = await BuildPageFromLoadResultAsync(autoLoginRetryResult, url, cancellationToken);
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
        // after browser fetch, the JS app may need more time to render. Re-fetch the page
        // content after an additional delay (NYT React app needs 3-5s to hydrate).
        if (!page.HasReadableContent() &&
            page.Classification == PageClassification.Article &&
            _lastLoadFetchMethod == FetchMethod.Browser &&
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
                _lastLoadFetchMethod = retryResult.FetchMethod;
                page = await BuildPageFromLoadResultAsync(retryResult, url, cancellationToken);
            }
        }

        // Headless challenge fallback: if headless browser got a bot challenge,
        // retry in headed mode where DataDome is less likely to block.
        // Skip for LinkList pages — they load fine via HTTP.
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
                _lastLoadFetchMethod = headedResult.FetchMethod;
                page = await BuildPageFromLoadResultAsync(headedResult, url, cancellationToken);
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
        StoreBuildCache(url, page);

        // Clear loading status now that page is ready
        _loadingStatus = null;

        return page;
    }

    public Task<NavigationTree> BuildNavigationTreeAsync(Page page, CancellationToken cancellationToken = default)
    {
        if (page.LinkTree != null)
        {
            return Task.FromResult(page.LinkTree);
        }

        return _treeBuilder.BuildTreeAsync(new List<LinkInfo>(), cancellationToken);
    }

    public Task<ReadableContent?> ExtractReadableContentAsync(Page page, CancellationToken cancellationToken = default)
    {
        if (page.ReadableContent != null)
        {
            return Task.FromResult<ReadableContent?>(page.ReadableContent);
        }

        return _contentExtractor.ExtractAsync(page.RawHtml, page.Url, cancellationToken);
    }

    public Task RenderAsync(Page page, ViewMode mode, RenderOptions options, CancellationToken cancellationToken = default)
    {
        var context = _navigationService.CurrentContext;

        switch (mode)
        {
            case ViewMode.Hierarchical:
                _renderer.RenderHierarchical(page, context, options);
                break;

            case ViewMode.Readable:
                _lineCacheManager.EnsureLineCache(options);
                _renderer.RenderReadable(page, context, options, _lineCacheManager.CachedLines?.ToList());
                break;

            case ViewMode.CollectionList:
                _renderer.RenderCollectionList(
                    _commandContext.Collections?.ToList() ?? new List<Collection>(),
                    _navigationService.CollectionSelectedIndex,
                    _commandContext.DefaultCollectionId,
                    _navigationService.CollectionListScrollOffset,
                    options);
                break;

            case ViewMode.CollectionItems:
                var activeCollection = _navigationService.ActiveCollection;
                if (activeCollection != null)
                {
                    _renderer.RenderCollectionItems(
                        activeCollection,
                        _navigationService.CollectionItemSelectedIndex,
                        _navigationService.CollectionItemScrollOffset,
                        options);
                }

                break;

            case ViewMode.Launcher:
                _renderer.RenderLauncher(
                    _commandContext.Bookmarks?.ToList() ?? new List<Domain.Entities.Bookmarks.Bookmark>(),
                    _navigationService.LauncherSelectedIndex,
                    _navigationService.LauncherScrollOffset,
                    options);
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Main browser loop. Handles user input and navigation.
    /// Uses alternate screen buffer for clean TUI experience.
    /// </summary>
    public async Task RunAsync(string? initialUrl = null, CancellationToken cancellationToken = default)
    {
        // Handle SIGINT gracefully: dispose browser session and restore console before exit
        Console.CancelKeyPress += (_, e) =>
        {
            try
            {
                _browserSession.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing browser session during SIGINT");
            }

            Console.Write("\x1b[?1049l");
            Console.CursorVisible = true;
            e.Cancel = false;
        };

        try
        {
            // Enter alternate screen buffer and hide cursor
            Console.Write("\x1b[?1049h");
            Console.CursorVisible = false;

            // Start resize detection and pre-loading in the background
            _ = _resizeDetector.StartAsync(cancellationToken)
                .ContinueWith(
                    t => _logger.LogError(t.Exception, "Background service faulted: ResizeDetector"),
                    TaskContinuationOptions.OnlyOnFaulted);
            _ = _preloadService.StartAsync(cancellationToken)
                .ContinueWith(
                    t => _logger.LogError(t.Exception, "Background service faulted: PreloadService"),
                    TaskContinuationOptions.OnlyOnFaulted);

            var options = GetCurrentRenderOptions();

            if (!string.IsNullOrWhiteSpace(initialUrl))
            {
                // Explicit URL provided → load directly (existing behavior)
                await NavigateToAsync(initialUrl, options, cancellationToken);
            }
            else
            {
                // No URL → show launcher home screen
                await EnterLauncherAsync(options, cancellationToken);
            }

            // Non-interactive mode: page rendered, exit cleanly (no TTY)
            if (!_inputHandler.IsInteractive)
            {
                _logger.LogInformation("Non-interactive mode: page rendered, exiting");
                return;
            }

            // Main input loop — races user input against a periodic progress check
            // so the status bar can update live during background preloading.
            Task<NavigationCommand>? pendingInput = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                // Re-read terminal dimensions on every iteration to handle resize
                options = GetCurrentRenderOptions();

                // Start waiting for input if not already waiting
                pendingInput ??= _inputHandler.WaitForInputAsync(cancellationToken);

                // Build the race: input, timer, and optionally background load
                var raceTasks = new List<Task> { pendingInput, Task.Delay(500, cancellationToken) };
                if (_backgroundPageLoad != null)
                {
                    raceTasks.Add(_backgroundPageLoad);
                }

                var completed = await Task.WhenAny(raceTasks);

                if (completed == _backgroundPageLoad)
                {
                    // Background load completed — replace skeleton with real page
                    await CompleteBackgroundLoadAsync(options, cancellationToken);
                }
                else if (completed == pendingInput)
                {
                    // User input received — process it
                    var command = await pendingInput;
                    pendingInput = null;

                    // Gate commands during background loading: only Quit, GoBack, and
                    // passive commands are allowed. All others are silently ignored.
                    if (HasActiveBackgroundLoad() &&
                        command.Type is not CommandType.Quit
                            and not CommandType.GoBack
                            and not CommandType.NoOp
                            and not CommandType.TerminalResized)
                    {
                        // Ignore the command — user will see the loading screen
                        continue;
                    }

                    // Cancel background load on GoBack (HandleGoBack will pop the skeleton)
                    if (HasActiveBackgroundLoad() && command.Type == CommandType.GoBack)
                    {
                        CancelBackgroundLoad();
                    }

                    var shouldContinue = await HandleCommandAsync(command, options, cancellationToken);
                    if (!shouldContinue)
                    {
                        CancelBackgroundLoad();
                        break;
                    }
                }
                else
                {
                    // Timer elapsed — update loading screen or check preload progress
                    if (HasActiveBackgroundLoad() && _loadingStatus != null)
                    {
                        _renderer.RenderLoading(
                            _loadingStatus.Url,
                            _loadingStatus.Stage,
                            _loadingStatus.ElapsedMs);
                    }
                    else
                    {
                        await CheckAndRenderProgressAsync(cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Browser session cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in browser loop");

            try
            {
                _renderer.RenderError(ex.Message, _navigationService.CurrentPage?.Url ?? "unknown");
            }
            catch (Exception innerEx)
            {
                _logger.LogDebug(innerEx, "Failed to render error screen");
            }
        }
        finally
        {
            // Dispose browser session and background services
            try
            {
                _preloadService.Dispose();
                _idleDetector.Dispose();
                _browserSession.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing services during shutdown");
            }

            // Exit alternate screen buffer and restore cursor
            Console.Write("\x1b[?1049l");
            Console.CursorVisible = true;
        }
    }

    /// <summary>
    /// Returns the number of visible nodes in the hierarchical view.
    /// Divides available lines by card height to convert from line count to node count.
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

    private static int GetHierarchicalViewportHeight(RenderOptions options)
    {
        var layout = UI.Renderers.LinkTreeRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        return layout.VisibleRows;
    }

    /// <summary>
    /// Calculates the available viewport height for the reader view.
    /// The headline is now embedded in the scrollable line cache,
    /// so only the header (1 line) and anchored status bar (1 line) are reserved.
    /// </summary>
    private static int GetReaderViewportHeight(RenderOptions options)
    {
        return Math.Max(3, options.TerminalHeight - 2);
    }

    /// <summary>
    /// Creates a scoped ICollectionService instance for database operations.
    /// ICollectionService is registered as Scoped (depends on DbContext) while the orchestrator is Singleton.
    /// </summary>
    private static ICollectionService CreateCollectionService(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<ICollectionService>();
    }

    /// <summary>
    /// Calculates how many grid rows actually fit on screen from a given scroll offset,
    /// accounting for scroll indicators and variable-height group headers.
    /// Mirrors the rendering loop in LinkTreeRenderer.RenderLinkTree.
    /// </summary>
    private static int GetActualVisibleRowCount(
        IReadOnlyList<UI.Renderers.GridRow> gridRows,
        int startRow,
        UI.Renderers.LinkTreeLayout layout)
    {
        // maxLines matches what TerminalPageRenderer passes: termHeight - headerLines - statusBarLines
        var maxLines = layout.VisibleRows * layout.CellHeight;
        var linesUsed = 0;
        var rowsVisible = 0;

        // Scroll-up indicator takes 1 line when scrolled down
        if (startRow > 0)
        {
            linesUsed++;
        }

        for (var row = startRow; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];
            var groupCardHeight = layout.CellHeight >= 5 ? 3 : layout.CellHeight;
            var linesNeeded = gr.IsGroupHeader
                ? UI.Renderers.LinkTreeRenderer.GetLinesForGroupHeader(gr.Left, groupCardHeight)
                : layout.CellHeight;

            // Reserve 1 line for scroll-down indicator if more rows follow
            var hasMoreAfter = row + 1 < gridRows.Count;
            var available = hasMoreAfter ? maxLines - 1 : maxLines;

            if (linesUsed + linesNeeded > available)
            {
                break;
            }

            linesUsed += linesNeeded;
            rowsVisible++;
        }

        return Math.Max(1, rowsVisible);
    }

    /// <summary>
    /// Creates render options from current terminal dimensions.
    /// </summary>
    private RenderOptions GetCurrentRenderOptions()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        var use256 = string.Equals(colorTerm, "truecolor", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(colorTerm, "24bit", StringComparison.OrdinalIgnoreCase)
                  || !string.IsNullOrEmpty(colorTerm);
        return new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = height,
            MaxContentWidth = _commandContext.ContentWidthOverride.HasValue
                ? Math.Clamp(Math.Min(_commandContext.ContentWidthOverride.Value, width - 2), Math.Min(MinContentWidth, width - 2), MaxContentWidth)
                : Math.Clamp(width - 2, Math.Min(MinContentWidth, width - 2), MaxContentWidth),
            Use256Colors = use256,
            CachedUrls = GetMergedCachedUrls(),
            CacheProgress = _preloadService.GetProgress(),
            PodcastButtonState = GetPodcastButtonState(),
            CacheUsagePercent = GetCacheUsagePercent(),
        };
    }

    private bool IsTtsConfigured()
    {
        if (!_ttsServiceResolved)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                _ttsService = scope.ServiceProvider.GetService<ITtsService>();
            }
            catch
            {
                // Podcast services may not be registered
            }

            _ttsServiceResolved = true;
        }

        return _ttsService?.IsConfigured ?? false;
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
    /// Returns null if the service is not registered.
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
    /// Returns null if the service is not registered.
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

    /// <summary>
    /// Returns the union of page-cached URLs and article-cached URLs from preloading.
    /// Used by renderers to show cache indicators for collection items.
    /// </summary>
    private double GetCacheUsagePercent()
    {
        var stats = _pageCache.GetStats();
        return stats?.UsagePercent ?? 0;
    }

    private IReadOnlySet<string> GetMergedCachedUrls()
    {
        var pageCachedUrls = _pageCache.GetCachedUrls();
        var articleCachedUrls = _preloadService.GetArticleCachedUrls();

        if (articleCachedUrls.Count == 0)
        {
            return pageCachedUrls;
        }

        var merged = new HashSet<string>(pageCachedUrls);
        merged.UnionWith(articleCachedUrls);
        return merged;
    }

    /// <summary>
    /// Checks whether the given URL belongs to a known paywalled domain.
    /// Matches both exact domain (e.g., "nytimes.com") and subdomains (e.g., "www.nytimes.com").
    /// </summary>
    private bool IsPaywalledDomain(string url)
    {
        if (_browserConfig.PaywalledDomains.Length == 0)
        {
            return false;
        }

        try
        {
            var host = new Uri(url).Host;
            return _browserConfig.PaywalledDomains.Any(d =>
                host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the podcast button state integer for RenderOptions.
    /// 0=Idle, 2=Disabled, 3=Unconfigured, 4=Selected (CTA focused via j/k).
    /// </summary>
    private int GetPodcastButtonState()
    {
        // Empty collections → Disabled (dimmed/inactive button)
        if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems
            && _navigationService.ActiveCollection is { } col
            && col.Items.Count == 0)
        {
            return 2; // Disabled
        }

        if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems
            && _navigationService.CollectionItemSelectedIndex == -1)
        {
            return 4; // Selected
        }

        return IsTtsConfigured() ? 0 : 3; // Idle or Unconfigured
    }

    /// <summary>
    /// Rebuilds a Page from cached build results (extracted links, hierarchy, content).
    /// Creates a fresh NavigationTree with clean selection state.
    /// </summary>
    private Page RebuildPageFromBuildCache(PageBuildCache cache)
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
    /// Stores the last build result in the page cache so repeat visits skip extraction.
    /// Uses _lastBuildResult captured by BuildPageFromLoadResultAsync.
    /// </summary>
    private void StoreBuildCache(string url, Page page)
    {
        var buildCache = _lastBuildResult;
        _lastBuildResult = null;

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

        // Use shorter TTL for link-list pages (content changes frequently)
        if (page.Classification == PageClassification.LinkList)
        {
            _pageCache.ApplyLinkListTtl(url);
            _logger.LogInformation("StoreBuildCache: applied LinkList TTL for {Url}", url);
        }

        _logger.LogInformation("StoreBuildCache: stored for {Url} ({LinkCount} links)", url, buildCache.Links.Count);
    }

    /// <summary>
    /// Builds a Page entity from a PageLoadResult, extracting links, tree, and readable content.
    /// </summary>
    private async Task<Page> BuildPageFromLoadResultAsync(PageLoadResult loadResult, string requestedUrl, CancellationToken cancellationToken)
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

        var readable = await _contentExtractor.ExtractAsync(loadResult.Html, loadResult.Url ?? requestedUrl, cancellationToken);
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

        // Capture build inputs for StoreBuildCache (called at end of LoadPageAsync)
        _lastBuildResult = new PageBuildCache
        {
            Links = links,
            HierarchyConfig = hierarchyConfig,
            ReadableContent = readable,
            Metadata = metadata,
            FinalUrl = finalUrl,
            Classification = classification,
        };

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

        // No saved config — try AI analysis if configured
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
    /// Builds a Page entity directly from a cached ExtractedArticle, skipping all network I/O.
    /// Used when the article content cache has a hit for a collection item URL.
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

    private async Task NavigateToAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        // Cancel any existing background load
        CancelBackgroundLoad();

        _preloadService.Pause();

        try
        {
            // Fast path: build cache hit (Phase 1) or page cache hit — load synchronously
            _logger.LogInformation("NavigateToAsync: checking build cache for {Url}", url);
            var buildCache = _pageCache.TryGetBuildCache(url);
            if (buildCache != null)
            {
                _logger.LogInformation("NavigateToAsync: build cache HIT for {Url}, skipping extraction", url);
                _lastLoadFetchMethod = FetchMethod.Cached;

                // Refresh TTL so link-list pages stay cached across revisits
                if (buildCache.Classification == PageClassification.LinkList)
                {
                    _pageCache.ApplyLinkListTtl(url);
                }

                var page = RebuildPageFromBuildCache(buildCache);
                await CompleteNavigation(page, url, options);
                return;
            }

            var htmlCached = _pageCache.Contains(url);
            _logger.LogInformation("NavigateToAsync: HTML cache {Result} for {Url}", htmlCached ? "HIT" : "MISS", url);
            if (htmlCached)
            {
                // HTML cached but no build cache — extraction is fast enough to do synchronously
                var page = await LoadPageAsync(url, cancellationToken);
                await CompleteNavigation(page, url, options);
                return;
            }

            // Slow path: cache miss — show skeleton page, load in background
            _logger.LogInformation("NavigateToAsync: full cache miss, showing skeleton for {Url}", url);
            ShowSkeletonPage(url);
            StartBackgroundLoad(url, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error navigating to {Url}", url);
            _renderer.RenderError(ex.Message, url);
            _preloadService.Resume();
        }
    }

    /// <summary>
    /// Completes a synchronous navigation: updates navigation state, renders, and resumes preloading.
    /// </summary>
    private async Task CompleteNavigation(Page page, string url, RenderOptions options)
    {
        try
        {
            _navigationService.NavigateTo(page);

            // Auto-switch to reader view for article pages with readable content
            if (page.Classification == PageClassification.Article && page.HasReadableContent())
            {
                _navigationService.SetViewMode(ViewMode.Readable);
            }

            var isFromCache = _lastLoadFetchMethod == FetchMethod.Cached;
            var cachedAt = isFromCache ? _pageCache.GetCachedAt(url) : null;
            _navigationService.SetCacheInfo(isFromCache, cachedAt);
            _lineCacheManager.InvalidateLineCache();

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(options, CancellationToken.None);

            // Eagerly warm up the browser for paywalled domains
            var warmupBrowserAvailable = (_browserSession as IBrowserSession)?.IsBrowserAvailable ?? false;
            if (warmupBrowserAvailable && IsPaywalledDomain(url) && _lastLoadFetchMethod != FetchMethod.Browser)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation(
                            "Paywalled domain detected, warming up browser session: {Url}", url);
                        await _browserSession.WarmUpAsync();
                    }
                    catch (Exception warmupEx)
                    {
                        _logger.LogWarning(warmupEx, "Browser warmup failed for paywalled domain");
                    }
                });
            }
        }
        finally
        {
            _preloadService.Resume();
        }
    }

    /// <summary>
    /// Shows a minimal skeleton page so the user sees something immediately during background load.
    /// </summary>
    private void ShowSkeletonPage(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            var metadata = new PageMetadata { Title = host };
            var skeletonPage = Page.Create(url, "<html></html>", metadata);

            _navigationService.NavigateTo(skeletonPage);
            _navigationService.SetCacheInfo(false, null);
            _lineCacheManager.InvalidateLineCache();

            _renderer.RenderLoading(url);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show skeleton page for {Url}", url);
        }
    }

    /// <summary>
    /// Starts loading a page in the background. The main loop will pick up the result
    /// via _backgroundPageLoad in its WhenAny race.
    /// </summary>
    private void StartBackgroundLoad(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _backgroundLoadUrl = url;
        _backgroundLoadOptions = options;
        _backgroundLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _backgroundLoadCts.Token;

        _backgroundPageLoad = Task.Run(
            async () =>
            {
                try
                {
                    return await LoadPageAsync(url, token);
                }
                catch
                {
                    _preloadService.Resume();
                    throw;
                }
            },
            token);
    }

    /// <summary>
    /// Cancels any in-progress background page load.
    /// </summary>
    private void CancelBackgroundLoad()
    {
        if (_backgroundPageLoad == null)
        {
            return;
        }

        try
        {
            _backgroundLoadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _backgroundPageLoad = null;
        _backgroundLoadUrl = null;
        _backgroundLoadOptions = null;

        try
        {
            _backgroundLoadCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _backgroundLoadCts = null;
        _preloadService.Resume();
    }

    /// <summary>
    /// Called from the main loop when background page load completes.
    /// Replaces the skeleton page with the real page.
    /// </summary>
    private async Task CompleteBackgroundLoadAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        var loadTask = _backgroundPageLoad;
        var url = _backgroundLoadUrl;
        var loadOptions = _backgroundLoadOptions ?? options;

        _backgroundPageLoad = null;
        _backgroundLoadUrl = null;
        _backgroundLoadOptions = null;

        try
        {
            _backgroundLoadCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _backgroundLoadCts = null;

        if (loadTask == null || url == null)
        {
            return;
        }

        try
        {
            var page = await loadTask;

            // Only replace if we're still on the skeleton for this URL
            if (_navigationService.CurrentPage?.Url != url)
            {
                _logger.LogDebug("Background load completed but user navigated away from {Url}", url);
                _preloadService.Resume();
                return;
            }

            // Use ReplaceCurrent to avoid pushing skeleton onto back history
            _navigationService.ReplaceCurrent(page);

            // Auto-switch to reader view for article pages with readable content
            if (page.Classification == PageClassification.Article && page.HasReadableContent())
            {
                _navigationService.SetViewMode(ViewMode.Readable);
            }

            var isFromCache = _lastLoadFetchMethod == FetchMethod.Cached;
            var cachedAt = isFromCache ? _pageCache.GetCachedAt(url) : null;
            _navigationService.SetCacheInfo(isFromCache, cachedAt);
            _lineCacheManager.InvalidateLineCache();

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(loadOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background page load was cancelled for {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background page load failed for {Url}", url);

            // If still on skeleton, show error
            if (_navigationService.CurrentPage?.Url == url)
            {
                _renderer.RenderError(ex.Message, url);
            }
        }
        finally
        {
            _preloadService.Resume();
        }
    }

    private bool HasActiveBackgroundLoad()
    {
        return _backgroundPageLoad != null && !_backgroundPageLoad.IsCompleted;
    }

    private async Task ForceRefreshAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _preloadService.Pause();

        try
        {
            _renderer.RenderLoading(url);

            var loadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true },
                cancellationToken);

            if (!loadResult.Success)
            {
                throw new InvalidOperationException($"Failed to load page: {loadResult.ErrorMessage}");
            }

            var page = await BuildPageFromLoadResultAsync(loadResult, url, cancellationToken);

            _navigationService.ReplaceCurrent(page);
            _navigationService.SetCacheInfo(false, null);
            _lineCacheManager.InvalidateLineCache();

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error force-refreshing {Url}", url);
            _renderer.RenderError(ex.Message, url);
        }
        finally
        {
            _preloadService.Resume();
        }
    }

    private async Task InteractiveRefreshAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _preloadService.Pause();

        try
        {
            _renderer.RenderLoading(url);

            // Force headed browser for interactive refresh — skip HTTP, go straight to browser
            // so the user gets a visible window they can interact with (login, captcha, etc.)
            var loadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = false, ForceRefresh = true, ForceBrowser = true },
                cancellationToken);

            if (!loadResult.Success)
            {
                _renderer.RenderError($"Failed to load page: {loadResult.ErrorMessage}", url);
                return;
            }

            // Restore the browser window AFTER the headed browser is created and page loaded
            if (_browserSession is IBrowserSession browserSession)
            {
                await browserSession.RestoreWindowAsync();
            }

            // If bot challenge detected, use the challenge polling helper (force headed)
            var challengeResult = await HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken, headlessOverride: false);
            if (challengeResult != null)
            {
                loadResult = challengeResult;
            }

            // Show prompt and wait for user to accept or cancel
            _renderer.RenderInteractiveRefresh(url);

            var input = await _inputHandler.WaitForInputAsync(cancellationToken);
            if (input.Type == CommandType.GoBack)
            {
                // User pressed Esc — cancel, minimize browser
                if (_browserSession is IBrowserSession cancelSession)
                {
                    await cancelSession.MinimizeWindowAsync();
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                return;
            }

            // Minimize browser now that interaction is complete
            if (_browserSession is IBrowserSession interactiveSession)
            {
                await interactiveSession.MinimizeWindowAsync();
            }

            // Accept: build page, cache, and re-render
            var page = await BuildPageFromLoadResultAsync(loadResult, url, cancellationToken);

            _pageCache.Put(url, loadResult);
            _navigationService.ReplaceCurrent(page);
            _navigationService.SetCacheInfo(true, DateTime.UtcNow);
            _lineCacheManager.InvalidateLineCache();

            // Save cookies from the headed browser session (enables future browser
            // loads of paywalled articles to use the user's login)
            await SaveBrowserCookiesAsync(cancellationToken);

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during interactive refresh for {Url}", url);
            _renderer.RenderError(ex.Message, url);
        }
        finally
        {
            _preloadService.Resume();
        }
    }

    /// <summary>
    /// Handles bot challenge detection and polling in headed mode.
    /// Returns a resolved PageLoadResult if the challenge was solved, or null if not applicable.
    /// </summary>
    private async Task<PageLoadResult?> HandleBotChallengeIfNeededAsync(
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
    /// Saves cookies from the active headed browser session for future use.
    /// Enables subsequent browser loads to use the user's login on paywalled sites.
    /// </summary>
    private async Task SaveBrowserCookiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_browserSession is not IBrowserSession session || !session.HasActiveBrowser || !session.IsBrowserAvailable)
            {
                return;
            }

            var page = await session.GetOrCreatePageAsync(false);
            var playwrightCookies = await page.Context.CookiesAsync();
            var storedCookies = playwrightCookies.Select(c =>
                new Application.Interfaces.StoredCookie(
                    c.Name,
                    c.Value,
                    c.Domain ?? string.Empty,
                    c.Path ?? string.Empty,
                    c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).DateTime : null)).ToList();

            await _cookieManager.SaveCookiesAsync(storedCookies, cancellationToken);
            _logger.LogInformation("Saved {Count} browser cookies after interactive refresh", storedCookies.Count);

            // Refresh HTTP client cookies so the preloader can use them
            await _httpCookieRefresher.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save browser cookies (non-fatal)");
        }
    }

    /// <summary>
    /// Waits for the user to complete a manual login in the browser window.
    /// Polls the browser URL to detect when the user navigates away from the login page.
    /// After login is detected, captures cookies for future sessions.
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

    private async Task<bool> HandleCommandAsync(NavigationCommand command, RenderOptions options, CancellationToken cancellationToken)
    {
        _idleDetector.RecordActivity();

        // Auto-commit expired undo states
        await UndoCommandHandler.CommitIfExpired(_commandContext, cancellationToken);

        // For any command other than Undo, passive commands, or DeleteItem
        // (which handles its own undo commit), commit pending undo immediately
        if (command.Type is not CommandType.Undo
            and not CommandType.NoOp
            and not CommandType.TerminalResized
            and not CommandType.DeleteItem
            && _commandContext.PendingUndo != null)
        {
            await UndoCommandHandler.ClearOnAction(_commandContext, cancellationToken);
        }

        try
        {
            // Handle launcher-specific commands first
            if (_navigationService.InLauncherMode)
            {
                return await LauncherCommandHandler.Handle(_commandContext, command, options, cancellationToken);
            }

            var commandType = command.Type;

            switch (commandType)
            {
                case CommandType.Quit:
                    return false;

                case CommandType.MoveDown:
                    await NavigationCommandHandler.HandleMoveDown(_commandContext, options, cancellationToken);
                    break;
                case CommandType.MoveUp:
                    await NavigationCommandHandler.HandleMoveUp(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ExpandNode:
                    await NavigationCommandHandler.HandleExpandNode(_commandContext, options, cancellationToken);
                    break;
                case CommandType.CollapseNode:
                    await NavigationCommandHandler.HandleCollapseNode(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ToggleNode:
                    await NavigationCommandHandler.HandleToggleNode(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ToggleSelection:
                    await NavigationCommandHandler.HandleToggleSelection(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ActivateLink:
                    await NavigationCommandHandler.HandleActivateLink(_commandContext, options, cancellationToken);
                    break;
                case CommandType.GoBack:
                    await NavigationCommandHandler.HandleGoBack(_commandContext, options, cancellationToken);
                    break;
                case CommandType.GoForward:
                    await NavigationCommandHandler.HandleGoForward(_commandContext, options, cancellationToken);
                    break;
                case CommandType.PageDown:
                    await NavigationCommandHandler.HandlePageDown(_commandContext, options, cancellationToken);
                    break;
                case CommandType.PageUp:
                    await NavigationCommandHandler.HandlePageUp(_commandContext, options, cancellationToken);
                    break;
                case CommandType.GoToTop:
                    await NavigationCommandHandler.HandleGoToTop(_commandContext, options, cancellationToken);
                    break;
                case CommandType.GoToBottom:
                    await NavigationCommandHandler.HandleGoToBottom(_commandContext, options, cancellationToken);
                    break;
                case CommandType.Refresh:
                    await NavigationCommandHandler.HandleRefresh(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ForceRefresh:
                    await NavigationCommandHandler.HandleForceRefresh(_commandContext, options, cancellationToken);
                    break;
                case CommandType.InteractiveRefresh:
                    await NavigationCommandHandler.HandleInteractiveRefresh(_commandContext, options, cancellationToken);
                    break;
                case CommandType.Navigate:
                    await NavigationCommandHandler.HandleNavigate(_commandContext, command, options, cancellationToken);
                    break;

                case CommandType.SwitchView:
                    await ViewCommandHandler.HandleSwitchView(_commandContext, options, cancellationToken);
                    break;
                case CommandType.SwitchToHierarchical:
                    await ViewCommandHandler.HandleSwitchToHierarchical(_commandContext, options, cancellationToken);
                    break;
                case CommandType.SwitchToReadable:
                    await ViewCommandHandler.HandleSwitchToReadable(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ShowHelp:
                    await ViewCommandHandler.HandleShowHelp(_commandContext, options, cancellationToken);
                    break;
                case CommandType.IncreaseWidth:
                    await ViewCommandHandler.HandleIncreaseWidth(_commandContext, options, cancellationToken);
                    break;
                case CommandType.DecreaseWidth:
                    await ViewCommandHandler.HandleDecreaseWidth(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ResetWidth:
                    await ViewCommandHandler.HandleResetWidth(_commandContext, options, cancellationToken);
                    break;
                case CommandType.OpenLauncher:
                    await ViewCommandHandler.HandleOpenLauncher(_commandContext, options, cancellationToken);
                    break;
                case CommandType.CycleTheme:
                    await ViewCommandHandler.HandleCycleTheme(_commandContext, options, cancellationToken);
                    break;
                case CommandType.TerminalResized:
                    await ViewCommandHandler.HandleTerminalResized(_commandContext, options, cancellationToken);
                    break;

                case CommandType.OpenCommandLine:
                    if (!await SearchCommandHandler.HandleOpenCommandLine(_commandContext, options, cancellationToken))
                    {
                        return false;
                    }

                    break;
                case CommandType.Search:
                    await SearchCommandHandler.HandleSearch(_commandContext, options, cancellationToken);
                    break;
                case CommandType.SearchNext:
                    await SearchCommandHandler.HandleSearchNext(_commandContext, options, cancellationToken);
                    break;
                case CommandType.SearchPrevious:
                    await SearchCommandHandler.HandleSearchPrevious(_commandContext, options, cancellationToken);
                    break;

                case CommandType.SaveToCollection:
                    await CollectionCommandHandler.HandleSaveToCollection(_commandContext, options, cancellationToken);
                    break;
                case CommandType.SaveToSpecific:
                    await CollectionCommandHandler.HandleSaveToSpecific(_commandContext, options, cancellationToken);
                    break;
                case CommandType.SaveAllToReadingList:
                    await CollectionCommandHandler.HandleSaveAllToReadingList(_commandContext, options, cancellationToken);
                    break;
                case CommandType.OpenCollections:
                    await CollectionCommandHandler.HandleOpenCollections(_commandContext, options, cancellationToken);
                    break;
                case CommandType.DeleteItem:
                    await CollectionCommandHandler.HandleDeleteItem(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ReorderUp:
                    await CollectionCommandHandler.HandleReorderUp(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ReorderDown:
                    await CollectionCommandHandler.HandleReorderDown(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ClearCollection:
                    await CollectionCommandHandler.HandleClearCollection(_commandContext, options, cancellationToken);
                    break;

                case CommandType.GeneratePodcast:
                    await PodcastCommandHandler.HandleGeneratePodcast(
                        _commandContext, options, cancellationToken);
                    break;

                case CommandType.DumpHtml:
                    await ViewCommandHandler.HandleDumpHtml(_commandContext, options, cancellationToken);
                    break;

                case CommandType.OpenInBrowser:
                    await ViewCommandHandler.HandleOpenInBrowser(_commandContext, options, cancellationToken);
                    break;

                case CommandType.AddBookmark:
                    // Only handle in launcher mode (handled above), ignore in other views
                    break;

                case CommandType.Undo:
                    await UndoCommandHandler.HandleUndo(_commandContext, options, cancellationToken);
                    break;
            }

            // Notify pre-loader of selection changes in hierarchical view
            NotifyPreloadSelectionChanged();

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command handler failed for {CommandType}", command.Type);
            _renderer.RenderError(ex.Message, _navigationService.CurrentPage?.Url ?? "unknown");
            return true;
        }
    }

    private async Task RenderCurrentPageAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var viewMode = _navigationService.CurrentContext.ViewMode;

            // Launcher view doesn't require a page - render directly
            if (viewMode == ViewMode.Launcher)
            {
                _renderer.RenderLauncher(
                    _commandContext.Bookmarks?.ToList() ?? new List<Domain.Entities.Bookmarks.Bookmark>(),
                    _navigationService.LauncherSelectedIndex,
                    _navigationService.LauncherScrollOffset,
                    options);
                return;
            }

            // Collection views don't require a page - render directly
            if (viewMode == ViewMode.CollectionList)
            {
                _renderer.RenderCollectionList(
                    _commandContext.Collections?.ToList() ?? new List<Collection>(),
                    _navigationService.CollectionSelectedIndex,
                    _commandContext.DefaultCollectionId,
                    _navigationService.CollectionListScrollOffset,
                    options);
                return;
            }

            if (viewMode == ViewMode.CollectionItems)
            {
                var activeCollection = _navigationService.ActiveCollection;
                if (activeCollection != null)
                {
                    _renderer.RenderCollectionItems(
                        activeCollection,
                        _navigationService.CollectionItemSelectedIndex,
                        _navigationService.CollectionItemScrollOffset,
                        options);
                }

                return;
            }

            var page = _navigationService.CurrentPage;
            if (page == null)
            {
                return;
            }

            await RenderAsync(page, viewMode, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Render failed for current page");

            try
            {
                var url = _navigationService.CurrentPage?.Url ?? "unknown";
                _renderer.RenderError($"Render error: {ex.Message}", url);
            }
            catch (Exception renderEx)
            {
                // Renderer itself is broken — write minimal fallback directly
                _logger.LogError(renderEx, "Fallback render also failed");

                try
                {
                    Console.Clear();
                    Console.WriteLine();
                    Console.WriteLine("  Error rendering page. Press any key to continue.");
                    Console.WriteLine($"  Details: {ex.Message}");
                    Console.WriteLine();
                }
                catch
                {
                    // Terminal may be in an unusable state; swallow to avoid crash
                }
            }
        }
    }

    /// <summary>
    /// Scrolls to a search match at the given match index.
    /// In reader view, uses line-based index when cache is available.
    /// In link view, selects the matching link node.
    /// </summary>
    private void ScrollToSearchMatch(int matchIndex, RenderOptions options)
    {
        var searchQuery = _navigationService.CurrentContext.SearchQuery;
        if (string.IsNullOrEmpty(searchQuery))
        {
            return;
        }

        var page = _navigationService.CurrentPage;
        if (page == null)
        {
            return;
        }

        if (_navigationService.CurrentContext.ViewMode == ViewMode.Readable && page.ReadableContent != null)
        {
            _lineCacheManager.EnsureLineCache(options);
            var cachedLines = _lineCacheManager.CachedLines;

            if (cachedLines != null)
            {
                // Search in pre-wrapped lines for precise line-based scrolling
                var matches = new List<int>();
                for (var i = 0; i < cachedLines.Count; i++)
                {
                    if (cachedLines[i].Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(i);
                    }
                }

                if (matches.Count > 0)
                {
                    var wrappedIndex = matchIndex % matches.Count;
                    if (wrappedIndex < 0)
                    {
                        wrappedIndex = matches.Count - 1;
                    }

                    _navigationService.SetSearchMatchIndex(wrappedIndex);
                    _navigationService.SetScrollOffset(matches[wrappedIndex]);
                }
            }
            else
            {
                // Fallback to paragraph-based search
                var matches = new List<int>();
                for (var i = 0; i < page.ReadableContent.Paragraphs.Count; i++)
                {
                    if (page.ReadableContent.Paragraphs[i].Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(i);
                    }
                }

                if (matches.Count > 0)
                {
                    var wrappedIndex = matchIndex % matches.Count;
                    if (wrappedIndex < 0)
                    {
                        wrappedIndex = matches.Count - 1;
                    }

                    _navigationService.SetSearchMatchIndex(wrappedIndex);
                    _navigationService.SetScrollOffset(matches[wrappedIndex]);
                }
            }
        }
        else if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical && page.LinkTree != null)
        {
            // Find link nodes matching the query
            var visibleNodes = page.LinkTree.GetVisibleNodes().ToList();
            var matches = new List<int>();
            for (var i = 0; i < visibleNodes.Count; i++)
            {
                if (visibleNodes[i].Link.DisplayText.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(i);
                }
            }

            if (matches.Count > 0)
            {
                var wrappedIndex = matchIndex % matches.Count;
                if (wrappedIndex < 0)
                {
                    wrappedIndex = matches.Count - 1;
                }

                _navigationService.SetSearchMatchIndex(wrappedIndex);
                var nodeIndex = matches[wrappedIndex];
                page.LinkTree.SelectNodeById(visibleNodes[nodeIndex].Id);
                AdjustScrollForSelection(page.LinkTree, options);
            }
        }
    }

    /// <summary>
    /// Adjusts scroll offset to keep the currently selected node visible on screen.
    /// </summary>
    private void AdjustScrollForSelection(NavigationTree? tree, RenderOptions options)
    {
        if (tree == null)
        {
            return;
        }

        var visibleNodes = tree.GetVisibleNodes().ToList();
        var selectedNode = tree.CurrentSelection;
        if (selectedNode == null)
        {
            return;
        }

        var selectedIndex = visibleNodes.IndexOf(selectedNode);
        if (selectedIndex < 0)
        {
            return;
        }

        var layout = UI.Renderers.LinkTreeRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        var gridRows = UI.Renderers.LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var (gridRow, _) = UI.Renderers.LinkTreeGridMapper.NodeIndexToGridPosition(gridRows, selectedIndex);

        var currentOffset = _navigationService.CurrentContext.ScrollOffset;
        var contentHeight = GetActualVisibleRowCount(gridRows, currentOffset, layout);

        if (gridRow < currentOffset)
        {
            // Scroll up: keep 1 row of context above when possible
            _navigationService.SetScrollOffset(Math.Max(0, gridRow - 1));
        }
        else if (gridRow >= currentOffset + contentHeight)
        {
            // Scroll down: keep 1 row of margin below the selected item
            // so multi-line cards aren't clipped at the viewport edge.
            var idealOffset = gridRow - contentHeight + 2;
            var maxOffset = Math.Max(0, gridRows.Count - contentHeight);
            _navigationService.SetScrollOffset(Math.Min(idealOffset, maxOffset));
        }
    }

    /// <summary>
    /// Called on a background thread when the preload service caches a new page.
    /// Sets a dirty flag that the main input loop checks periodically.
    /// </summary>
    private void OnPreloadProgressChanged()
    {
        _progressDirty = true;
    }

    /// <summary>
    /// Checks whether preload progress has changed and enough time has elapsed
    /// since the last progress-driven render (debounce to at most once per second).
    /// If so, re-renders the current page to update the status bar.
    /// </summary>
    private async Task CheckAndRenderProgressAsync(CancellationToken cancellationToken)
    {
        if (!_progressDirty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastProgressRender).TotalMilliseconds < 1000)
        {
            return;
        }

        _progressDirty = false;
        _lastProgressRender = now;

        // Only refresh for views that display preload progress
        var viewMode = _navigationService.CurrentContext.ViewMode;
        if (viewMode != ViewMode.Hierarchical && viewMode != ViewMode.CollectionItems)
        {
            return;
        }

        try
        {
            // Re-read options to get fresh CacheProgress
            var freshOptions = GetCurrentRenderOptions();
            await RenderCurrentPageAsync(freshOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error rendering progress update");
        }
    }

    /// <summary>
    /// Notifies the pre-load service of the current selection, adapting to the active view mode.
    /// </summary>
    private void NotifyPreloadSelectionChanged()
    {
        var viewMode = _navigationService.CurrentContext.ViewMode;

        switch (viewMode)
        {
            case ViewMode.CollectionItems:
            {
                var collection = _navigationService.ActiveCollection;
                if (collection == null || collection.Items.Count == 0)
                {
                    return;
                }

                var urls = collection.Items.Select(item => item.Url).ToList();
                _preloadService.NotifyCollectionChanged(
                    _navigationService.CollectionItemSelectedIndex,
                    urls);
                break;
            }

            case ViewMode.Hierarchical:
            {
                // When viewing an article opened from a collection, let the collection queue continue
                if (_navigationService.HasCollectionReturnPoint)
                {
                    return;
                }

                var page = _navigationService.CurrentPage;
                var tree = page?.LinkTree;
                if (tree == null)
                {
                    return;
                }

                var allNodes = tree.GetAllNodes().ToList();
                var selectedNode = tree.CurrentSelection;
                var selectedIndex = selectedNode != null ? allNodes.IndexOf(selectedNode) : 0;

                _preloadService.NotifySelectionChanged(
                    Math.Max(0, selectedIndex),
                    allNodes,
                    page!.Url);
                break;
            }

            case ViewMode.Readable:
            {
                // When reading an article opened from a collection, let the collection queue continue
                if (_navigationService.HasCollectionReturnPoint)
                {
                    return;
                }

                break;
            }

            case ViewMode.Launcher:
            case ViewMode.CollectionList:
                _preloadService.ClearQueue();
                break;
        }
    }

    /// <summary>
    /// Enters the launcher home screen.
    /// </summary>
    private async Task EnterLauncherAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        try
        {
            _navigationService.EnterLauncher();
            await RefreshBookmarksAsync(cancellationToken);
            await RenderCurrentPageAsync(options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enter launcher mode");
        }
    }

    /// <summary>
    /// Refreshes the cached bookmarks from the database, seeding defaults on first run.
    /// </summary>
    private async Task RefreshBookmarksAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
            await bookmarkService.EnsureSeededAsync(cancellationToken);
            var all = await bookmarkService.GetAllBookmarksAsync(cancellationToken);
            _commandContext.Bookmarks = all.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh bookmarks");
            _commandContext.Bookmarks ??= new List<Domain.Entities.Bookmarks.Bookmark>();
        }
    }

    /// <summary>
    /// Refreshes the cached collections data from the service.
    /// Also updates the active collection reference if viewing collection items.
    /// </summary>
    private async Task RefreshCollectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var collectionService = CreateCollectionService(scope);

            // Purge expired reading list items (16-hour TTL) before loading
            await collectionService.PurgeExpiredReadingListItemsAsync(
                TimeSpan.FromHours(16), cancellationToken);

            var allCollections = await collectionService.GetAllCollectionsAsync(cancellationToken);
            _commandContext.Collections = allCollections.ToList();

            var defaultCollection = await collectionService.GetDefaultCollectionAsync(cancellationToken);
            _commandContext.DefaultCollectionId = defaultCollection.Id;

            // Update active collection reference if we're viewing one
            if (_navigationService.ActiveCollection != null)
            {
                var savedItemIndex = _navigationService.CollectionItemSelectedIndex;
                var collections = _commandContext.Collections;
                var updatedActive = collections?.FirstOrDefault(c => c.Id == _navigationService.ActiveCollection.Id);
                if (updatedActive != null)
                {
                    // Use UpdateActiveCollection to preserve scroll offset and UI position
                    _navigationService.UpdateActiveCollection(updatedActive);

                    if (updatedActive.Items.Count == 0)
                    {
                        // Empty collection: CTA index (-1) is invalid, reset to 0
                        _navigationService.CollectionItemSelectedIndex = 0;
                    }
                    else
                    {
                        // Clamp saved index to valid range; preserve -1 (CTA) when items exist
                        var maxIndex = updatedActive.Items.Count - 1;
                        _navigationService.CollectionItemSelectedIndex =
                            savedItemIndex < 0 ? -1 : Math.Min(savedItemIndex, maxIndex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh collections");
        }
    }
}
