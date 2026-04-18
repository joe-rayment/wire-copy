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

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Orchestrates the terminal browser functionality.
/// Coordinates page loading, navigation, rendering, and input handling.
/// </summary>
public class BrowserOrchestrator : IBrowserService
{
    private const int MinContentWidth = 40;
    private const int MaxContentWidth = 120;
    private const int DefaultReaderWidth = 60;

    private readonly IPageLoader _pageLoader;
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
    private readonly PageLoadPipeline _pipeline;

    // Tracks FetchMethod from the last LoadPageAsync call for NavigateToAsync
    private FetchMethod _lastLoadFetchMethod;

    // Background page load state for non-blocking navigation (Phase 2)
    private Task<Page>? _backgroundPageLoad;
    private string? _backgroundLoadUrl;
    private CancellationTokenSource? _backgroundLoadCts;
    private RenderOptions? _backgroundLoadOptions;
    private volatile LoadingStatus? _loadingStatus;

    // Lazily resolved TTS service for checking IsConfigured state
    private ITtsService? _ttsService;
    private bool _ttsServiceResolved;

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
        ILogger<BrowserOrchestrator> logger,
        PageLoadPipeline pipeline)
    {
        _pageLoader = pageLoader;
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
        _pipeline = pipeline;
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
        var loadTimer = System.Diagnostics.Stopwatch.StartNew();
        void ReportStage(string stage) => _loadingStatus = new LoadingStatus
        {
            Stage = stage,
            ElapsedMs = loadTimer.ElapsedMilliseconds,
            Url = url,
        };

        var result = await _pipeline.LoadAsync(url, ReportStage, cancellationToken);
        _lastLoadFetchMethod = result.FetchMethod;

        // Clear loading status now that page is ready
        _loadingStatus = null;

        return result.Page;
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
                if (_renderer is UI.TerminalPageRenderer tpr)
                {
                    tpr.SetParagraphSpans(_lineCacheManager.ParagraphSpans);
                }

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

            Console.Write("\x1b[?1006l\x1b[?1000l\x1b[?1049l");
            Console.CursorVisible = true;
            e.Cancel = false;
        };

        try
        {
            // Enter alternate screen buffer, enable mouse tracking, and hide cursor
            Console.Write("\x1b[?1049h\x1b[?1000h\x1b[?1006h");
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
            // When speed reading is active, a per-line delay races as a fourth task.
            Task<NavigationCommand>? pendingInput = null;
            Task? speedReadDelay = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                // Re-read terminal dimensions on every iteration to handle resize
                options = GetCurrentRenderOptions();

                // Start waiting for input if not already waiting
                pendingInput ??= _inputHandler.WaitForInputAsync(cancellationToken);

                // Build the race: input, timer, and optionally background load + speed read
                var raceTasks = new List<Task> { pendingInput, Task.Delay(500, cancellationToken) };
                if (_backgroundPageLoad != null)
                {
                    raceTasks.Add(_backgroundPageLoad);
                }

                // Persist speed reading timer across iterations (like pendingInput)
                // so it isn't reset by the 500ms status timer firing first.
                if (_navigationService.IsSpeedReadActive
                    && _navigationService.CurrentContext.ViewMode == ViewMode.Readable)
                {
                    speedReadDelay ??= Task.Delay(
                        ComputeLineDelayMs(
                            _navigationService.ReaderCursorLine,
                            _navigationService.SpeedReadWpm),
                        cancellationToken);

                    raceTasks.Add(speedReadDelay);
                }
                else
                {
                    speedReadDelay = null;
                }

                var completed = await Task.WhenAny(raceTasks);

                if (completed == _backgroundPageLoad)
                {
                    // Background load completed — replace skeleton with real page
                    await CompleteBackgroundLoadAsync(options, cancellationToken);
                }
                else if (speedReadDelay != null && completed == speedReadDelay)
                {
                    // Speed read timer fired — advance cursor one line
                    speedReadDelay = null; // Allow next line's timer to be created
                    if (!AdvanceSpeedReadCursor(options))
                    {
                        // Reached end of article
                        _navigationService.StopSpeedRead();
                    }

                    await RenderCurrentPageAsync(options, cancellationToken);
                }
                else if (completed == pendingInput)
                {
                    // User input received — process it
                    var command = await pendingInput;
                    pendingInput = null;

                    // During speed reading: f/</> control speed, any other key stops and is consumed
                    if (_navigationService.IsSpeedReadActive
                        && command.Type is not CommandType.ToggleSpeedRead
                            and not CommandType.SpeedReadFaster
                            and not CommandType.SpeedReadSlower)
                    {
                        _navigationService.StopSpeedRead();
                        speedReadDelay = null;
                        await RenderCurrentPageAsync(options, cancellationToken);
                        continue; // Consume the keypress
                    }

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

                    // Reset speed read timer after any command so WPM/toggle changes take effect
                    speedReadDelay = null;

                    if (!shouldContinue)
                    {
                        CancelBackgroundLoad();
                        break;
                    }
                }
                else
                {
                    // Timer elapsed — update loading status or check preload progress
                    if (HasActiveBackgroundLoad() && _loadingStatus != null)
                    {
                        var stage = _loadingStatus.Stage ?? "Loading...";
                        var elapsed = _loadingStatus.ElapsedMs / 1000;
                        _navigationService.SetStatusMessage($"{stage} ({elapsed}s)");
                        await RenderCurrentPageAsync(options, cancellationToken);
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

            // Disable mouse tracking and exit alternate screen buffer
            Console.Write("\x1b[?1006l\x1b[?1000l\x1b[?1049l");
            Console.CursorVisible = true;
        }
    }

    private static int GetHierarchicalViewportHeight(RenderOptions options)
    {
        var layout = UI.Renderers.LinkTreeRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        return layout.VisibleRows;
    }

    /// <summary>
    /// Calculates the available viewport height for the reader view.
    /// The headline is now embedded in the scrollable line cache,
    /// so only the header (1), separator rule (1), and anchored status bar (1) are reserved.
    /// </summary>
    private static int GetReaderViewportHeight(RenderOptions options)
    {
        return Math.Max(3, options.TerminalHeight - 3);
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
            MaxContentWidth = ComputeContentWidth(width),
            Use256Colors = use256,
            CachedUrls = GetMergedCachedUrls(),
            CacheProgress = _preloadService.GetProgress(),
            PodcastButtonState = GetPodcastButtonState(),
            CacheUsagePercent = GetCacheUsagePercent(),
        };
    }

    private int ComputeContentWidth(int terminalWidth)
    {
        var min = Math.Min(MinContentWidth, terminalWidth - 2);

        if (_commandContext.ContentWidthOverride.HasValue)
        {
            return Math.Clamp(
                Math.Min(_commandContext.ContentWidthOverride.Value, terminalWidth - 2),
                min,
                MaxContentWidth);
        }

        // In reader view, use a narrower default for readability
        var isReaderView = _navigationService.CurrentContext.ViewMode == ViewMode.Readable;
        var defaultWidth = isReaderView
            ? Math.Min(DefaultReaderWidth, terminalWidth - 2)
            : terminalWidth - 2;

        return Math.Clamp(defaultWidth, min, MaxContentWidth);
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

                var page = _pipeline.RebuildFromBuildCache(buildCache);
                await CompleteNavigation(page, url, options);
                return;
            }

            var htmlCached = _pageCache.Contains(url);
            _logger.LogInformation("NavigateToAsync: build cache MISS, HTML cache {Result} for {Url}", htmlCached ? "HIT" : "MISS", url);
            if (htmlCached)
            {
                // HTML cached — extraction is fast (especially for LinkList with content skip).
                // Load synchronously to avoid skeleton flash.
                var page = await LoadPageAsync(url, cancellationToken);
                await CompleteNavigation(page, url, options);
                return;
            }

            // Full cache miss — show skeleton page, load in background
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
            if (warmupBrowserAvailable && _browserConfig.IsPaywalledDomain(url) && _lastLoadFetchMethod != FetchMethod.Browser)
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

            // Set as LinkList with empty tree so the hierarchical view renders
            // the page frame (header, status bar) with a loading status instead
            // of a blank "Loading..." screen. User sees the page structure immediately.
            skeletonPage.SetClassification(PageClassification.LinkList);
            var emptyTree = NavigationTree.BuildFromRoot(LinkNode.CreateRoot());
            skeletonPage.SetLinkTree(emptyTree);

            _navigationService.NavigateTo(skeletonPage);
            _navigationService.SetViewMode(ViewMode.Hierarchical);
            _navigationService.SetCacheInfo(false, null);
            _navigationService.SetStatusMessage("Loading...");
            _lineCacheManager.InvalidateLineCache();

            _renderer.RenderHierarchical(
                skeletonPage,
                _navigationService.CurrentContext,
                GetCurrentRenderOptions());
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

            var (page, _) = await _pipeline.BuildPageAsync(loadResult, url, cancellationToken);

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
            var challengeResult = await _pipeline.HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken, headlessOverride: false);
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
            var (page, _) = await _pipeline.BuildPageAsync(loadResult, url, cancellationToken);

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
                case CommandType.ParagraphDown:
                    await NavigationCommandHandler.HandleParagraphDown(_commandContext, options, cancellationToken);
                    break;
                case CommandType.ParagraphUp:
                    await NavigationCommandHandler.HandleParagraphUp(_commandContext, options, cancellationToken);
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

                case CommandType.ToggleSpeedRead:
                    if (_navigationService.CurrentContext.ViewMode == ViewMode.Readable)
                    {
                        if (_navigationService.IsSpeedReadActive)
                        {
                            _navigationService.StopSpeedRead();
                        }
                        else if (_navigationService.CurrentPage?.HasReadableContent() == true)
                        {
                            _navigationService.StartSpeedRead();
                        }
                        else
                        {
                            _navigationService.SetStatusMessage("No readable content for speed reading");
                        }

                        await RenderCurrentPageAsync(options, cancellationToken);
                    }

                    break;
                case CommandType.SpeedReadFaster:
                    _navigationService.AdjustSpeedReadWpm(25);
                    _navigationService.SetStatusMessage($"{_navigationService.SpeedReadWpm} WPM");
                    await RenderCurrentPageAsync(options, cancellationToken);
                    break;
                case CommandType.SpeedReadSlower:
                    _navigationService.AdjustSpeedReadWpm(-25);
                    _navigationService.SetStatusMessage($"{_navigationService.SpeedReadWpm} WPM");
                    await RenderCurrentPageAsync(options, cancellationToken);
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

    #pragma warning disable S1172 // Parameter kept for delegate signature compatibility
    private async Task RenderCurrentPageAsync(RenderOptions options, CancellationToken cancellationToken)
    #pragma warning restore S1172
    {
        try
        {
            // Recompute options so view-mode-dependent values (e.g. reader width) are fresh
            options = GetCurrentRenderOptions();
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
                    _navigationService.SetReaderCursorLine(matches[wrappedIndex]);
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
                    _navigationService.SetReaderCursorLine(matches[wrappedIndex]);
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

    /// <summary>
    /// Computes the delay in milliseconds for a given line during speed reading.
    /// Delegates to the line cache to find line content and next-line context.
    /// </summary>
    private int ComputeLineDelayMs(int lineIndex, int wpm)
    {
        var lines = _lineCacheManager.CachedLines;
        if (lines == null || lines.Count == 0 || lineIndex < 0 || lineIndex >= lines.Count)
        {
            return 300;
        }

        var nextLineBlank = lineIndex + 1 < lines.Count && string.IsNullOrEmpty(lines[lineIndex + 1]);
        return ComputeLineDelayMs(lines[lineIndex], wpm, nextLineBlank);
    }

    /// <summary>
    /// Pure computation: delay in ms for a line based on word count and WPM.
    /// If nextLineBlank (paragraph boundary), adds a 300ms pause.
    /// Minimum 50ms floor.
    /// </summary>
#pragma warning disable SA1202 // Internal test helper placed near its private caller
    internal static int ComputeLineDelayMs(string line, int wpm, bool nextLineBlank)
    {
        var wordCount = CountWordsStrippingAnsi(line);
        if (wordCount == 0)
        {
            return 50;
        }

        var msPerWord = 60000.0 / wpm;
        var delayMs = (int)(wordCount * msPerWord);

        if (nextLineBlank)
        {
            delayMs += 300;
        }

        return Math.Max(50, delayMs);
    }

    /// <summary>
    /// Advances the speed reading cursor one line forward, skipping blank lines.
    /// Paragraph pauses are handled by ComputeLineDelayMs adding extra delay
    /// when the current line precedes a blank. Returns false if at end of article.
    /// </summary>
    private bool AdvanceSpeedReadCursor(RenderOptions options)
    {
        _lineCacheManager.EnsureLineCache(options);
        var lines = _lineCacheManager.CachedLines;
        if (lines == null || lines.Count == 0)
        {
            return false;
        }

        var cursor = _navigationService.ReaderCursorLine;
        var totalLines = lines.Count;

        var newCursor = cursor + 1;

        // Skip blank lines
        while (newCursor < totalLines && string.IsNullOrEmpty(lines[newCursor]))
        {
            newCursor++;
        }

        if (newCursor >= totalLines)
        {
            return false; // End of article
        }

        _navigationService.SetReaderCursorLine(newCursor);

        // Adjust scroll to keep cursor visible
        var scroll = _navigationService.CurrentContext.ScrollOffset;
        var vpHeight = GetReaderViewportHeight(options);
        var maxScroll = Math.Max(0, totalLines - vpHeight);

        const int bottomMargin = 2;
        if (newCursor > scroll + vpHeight - 1 - bottomMargin)
        {
            scroll = Math.Min(maxScroll, newCursor - vpHeight + 1 + bottomMargin);
            _navigationService.SetScrollOffset(Math.Clamp(scroll, 0, maxScroll));
        }

        return true;
    }

    internal static int CountWordsStrippingAnsi(string text)
    {
        var wordCount = 0;
        var inWord = false;
        var i = 0;
        while (i < text.Length)
        {
            // Skip ANSI escape sequences
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                i += 2;
                while (i < text.Length && text[i] != 'm')
                {
                    i++;
                }

                if (i < text.Length)
                {
                    i++; // skip 'm'
                }

                continue;
            }

            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                wordCount++;
            }

            i++;
        }

        return wordCount;
    }
#pragma warning restore SA1202
}
