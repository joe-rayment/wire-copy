// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Orchestrates the terminal browser functionality.
/// Coordinates page loading, navigation, rendering, and input handling.
/// </summary>
public partial class BrowserOrchestrator : IBrowserService
{
    private const int MinContentWidth = 40;
    private const int MaxContentWidth = 120;

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
    private readonly ILayoutVariantProvider _layoutVariantProvider;
    private readonly IThemeProvider _themeProvider;

    // Tracks FetchMethod from the last LoadPageAsync call for NavigateToAsync
    private FetchMethod _lastLoadFetchMethod;

    // Background page load state for non-blocking navigation (Phase 2)
    private Task<Page>? _backgroundPageLoad;
    private string? _backgroundLoadUrl;
    private CancellationTokenSource? _backgroundLoadCts;
    private RenderOptions? _backgroundLoadOptions;
    private volatile LoadingStatus? _loadingStatus;

    // Background quality retry state (progressive loading)
    private Task<PageLoadPipelineResult>? _qualityRetryTask;
    private string? _qualityRetryUrl;

    // workspace-iv9g: background watcher that polls the headed browser after
    // a captcha / login / cookie-consent verdict is rendered. When the user
    // solves the gate in their browser window, the watcher detects the
    // arrival of real content and re-runs the page load so the app picks it
    // up automatically — no manual Shift+R / back+reload step. Null when no
    // human action is currently being awaited.
    private Task<PageLoadResult?>? _humanActionWatcher;
    private string? _humanActionWatcherUrl;
    private CancellationTokenSource? _humanActionWatcherCts;

    // Lazily resolved TTS service for checking IsConfigured state
    private ITtsService? _ttsService;
    private bool _ttsServiceResolved;

    // Live preload progress indicator: set by background thread, read by main loop
    private volatile bool _progressDirty;
    private DateTime _lastProgressRender = DateTime.MinValue;

    // Cache animation state: tracks previous cached count to detect new-item and warm-complete transitions
    private volatile int _prevCachedCount;
    private volatile bool _prevIsComplete;

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
        PageLoadPipeline pipeline,
        ILayoutVariantProvider layoutVariantProvider)
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
        _layoutVariantProvider = layoutVariantProvider;
        _themeProvider = themeProvider;
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
            LayoutVariantProvider = layoutVariantProvider,
            DisableAnimations = _browserConfig.DisableAnimations,
            NavigateToAsync = NavigateToAsync,
            ForceRefreshAsync = ForceRefreshAsync,
            InteractiveRefreshAsync = InteractiveRefreshAsync,
            OpenInteractiveBrowserAsync = OpenInteractiveBrowserAsync,
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

        var result = await _pipeline.LoadAsync(url, ReportStage, cancellationToken).ConfigureAwait(false);
        _lastLoadFetchMethod = result.FetchMethod;

        // Store background quality retry task if the pipeline scheduled one
        if (result.QualityRetryTask != null)
        {
            _qualityRetryTask = result.QualityRetryTask;
            _qualityRetryUrl = url;
        }

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

        // workspace-1f5a: compose the entire frame into a single buffer and
        // emit it via one Console.Out.Write so OS pipe-buffer flushes can't
        // split an SGR escape sequence mid-write (which was dropping the
        // cursor highlight on the user's terminal under rapid keystrokes).
        _renderer.BeginFrame();
        try
        {
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
        }
        finally
        {
            _renderer.EndFrame();
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

            Console.Write("\x1b[?2004l\x1b[?1006l\x1b[?1000l\x1b[?1049l");
            Console.CursorVisible = true;
            e.Cancel = false;
        };

        try
        {
            // Enter alternate screen buffer, enable mouse tracking, hide cursor,
            // and turn on bracketed-paste mode (\x1b[?2004h) so multi-line pastes
            // arrive wrapped in \x1b[200~ ... \x1b[201~ markers our input handler
            // can recognize and stream as a single chunk.
            Console.Write("\x1b[?1049h\x1b[?1000h\x1b[?1006h\x1b[?2004h");
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
                await NavigateToAsync(initialUrl, options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // No URL → show launcher home screen. First-run users land here
                // too: reading flows work without API keys, and the launcher's
                // header card surfaces a non-focusable "press c" Setup hint
                // (workspace-ayt8 + workspace-jby8). Default focus stays on the
                // URL bar so Enter submits a URL and c opens Setup.
                await EnterLauncherAsync(options, cancellationToken).ConfigureAwait(false);
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

                // workspace-vkhr Phase D acceptance: if a detached podcast
                // generation has finished while the user is on a regular view,
                // pop the completion / error screen automatically. We can only
                // be in this top-level loop when no modal is on screen, so a
                // completed-but-uncleared job here means "detached run reached
                // the finish line; user must see the result." Hand off to
                // HandleRestorePodcastModal which attaches to the existing job,
                // renders the result, and calls jobManager.Clear() so the
                // status-bar badge stops showing "Generating 100%" forever.
                if (await CheckDetachedPodcastCompletionAsync(options, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                // Start waiting for input if not already waiting
                pendingInput ??= _inputHandler.WaitForInputAsync(cancellationToken);

                // Build the race: input, timer, and optionally background load + speed read + quality retry
                var raceTasks = new List<Task> { pendingInput, Task.Delay(500, cancellationToken) };
                if (_backgroundPageLoad != null)
                {
                    raceTasks.Add(_backgroundPageLoad);
                }

                if (_qualityRetryTask != null)
                {
                    raceTasks.Add(_qualityRetryTask);
                }

                // workspace-iv9g: race the human-action watcher (captcha auto-
                // resume) so the main loop wakes immediately when the user
                // solves the gate in their browser window.
                if (_humanActionWatcher != null)
                {
                    raceTasks.Add(_humanActionWatcher);
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

                    // Reset measured render overhead so the next speed-read session
                    // starts from the default estimate rather than a stale value.
                    _lastLineRenderMs = DefaultRenderOverheadMs;
                }

                var completed = await Task.WhenAny(raceTasks).ConfigureAwait(false);

                if (completed == _backgroundPageLoad)
                {
                    // Background load completed — replace skeleton with real page
                    await CompleteBackgroundLoadAsync(options, cancellationToken).ConfigureAwait(false);
                }
                else if (completed == _humanActionWatcher)
                {
                    // workspace-iv9g: the watcher finished — user either solved
                    // the gate (success result) or it timed out / cancelled
                    // (null). CompleteHumanActionWatcherAsync handles both.
                    await CompleteHumanActionWatcherAsync(options, cancellationToken).ConfigureAwait(false);
                }
                else if (completed == _qualityRetryTask)
                {
                    // Quality retry completed — replace page with improved version if better
                    await CompleteQualityRetryAsync(options, cancellationToken).ConfigureAwait(false);
                }
                else if (speedReadDelay != null && completed == speedReadDelay)
                {
                    // Speed read timer fired — advance cursor one line.
                    // Measure the actual render time so the NEXT line's delay
                    // can subtract real overhead (not a fixed 100ms guess) and
                    // the effective WPM matches the configured setting.
                    speedReadDelay = null; // Allow next line's timer to be created
                    var renderStopwatch = Stopwatch.StartNew();
                    if (!AdvanceSpeedReadCursor(options))
                    {
                        // Reached end of article
                        _navigationService.StopSpeedRead();
                    }

                    await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                    renderStopwatch.Stop();
                    _lastLineRenderMs = (int)renderStopwatch.ElapsedMilliseconds;
                }
                else if (completed == pendingInput)
                {
                    // User input received — process it
                    var command = await pendingInput.ConfigureAwait(false);
                    pendingInput = null;

                    // Animation tick: lightweight render update for animated regions only
                    if (command.Type == CommandType.AnimationTick)
                    {
                        await HandleAnimationTickAsync(options, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // During speed reading: f/</> control speed, any other key stops and is consumed
                    if (_navigationService.IsSpeedReadActive
                        && command.Type is not CommandType.ToggleSpeedRead
                            and not CommandType.SpeedReadFaster
                            and not CommandType.SpeedReadSlower)
                    {
                        _navigationService.StopSpeedRead();
                        speedReadDelay = null;
                        await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                        continue; // Consume the keypress
                    }

                    // Gate commands during background loading: only Quit, GoBack, and
                    // passive commands are allowed. All others are silently ignored.
                    if (HasActiveBackgroundLoad() &&
                        command.Type is not CommandType.Quit
                            and not CommandType.GoBack
                            and not CommandType.NoOp
                            and not CommandType.TerminalResized
                            and not CommandType.AnimationTick)
                    {
                        // Ignore the command — user will see the loading screen
                        continue;
                    }

                    // Cancel background load on GoBack (HandleGoBack will pop the skeleton)
                    if (HasActiveBackgroundLoad() && command.Type == CommandType.GoBack)
                    {
                        CancelBackgroundLoad();
                    }

                    var shouldContinue = await HandleCommandAsync(command, options, cancellationToken).ConfigureAwait(false);

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
                        await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await CheckAndRenderProgressAsync(cancellationToken).ConfigureAwait(false);
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

            // Disable bracketed-paste, mouse tracking, and exit alternate screen buffer
            Console.Write("\x1b[?2004l\x1b[?1006l\x1b[?1000l\x1b[?1049l");
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
    /// so only the header (1) and 2-line status bar (separator + content) are reserved.
    /// </summary>
    private static int GetReaderViewportHeight(RenderOptions options)
    {
        // workspace-umi7: route through the shared ReaderLayout so the speed-read
        // scroller's threshold matches what the renderer actually paints. The old
        // `terminalHeight - 3` assumed a 1-line header but the title bar writes 3.
        return UI.ReaderLayout.ComputeContentHeight(options.TerminalHeight);
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

    private async Task NavigateToAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        // Cancel any existing background load and quality retry
        CancelBackgroundLoad();
        _qualityRetryTask = null;
        _qualityRetryUrl = null;

        _preloadService.Pause();

        try
        {
            // Fast path: build cache hit (Phase 1) or page cache hit — load synchronously
            _logger.LogInformation("NavigateToAsync: checking build cache for {Url}", url);
            var buildCache = _pageCache.TryGetBuildCache(url);
            if (buildCache != null)
            {
                // Quality gate: reject build caches with stale classification logic
                if (buildCache.ClassificationVersion != PageClassifier.ClassificationVersion)
                {
                    _logger.LogInformation(
                        "NavigateToAsync: rejecting stale build cache (classification v{Old} != v{New}): {Url}",
                        buildCache.ClassificationVersion,
                        PageClassifier.ClassificationVersion,
                        url);
                    _pageCache.Remove(url);
                }
                else if (buildCache.Classification == PageClassification.LinkList
                    && !PageClassifier.IsSectionUrlPattern(url))
                {
                    _logger.LogInformation(
                        "NavigateToAsync: rejecting stale build cache (LinkList for non-section URL): {Url}", url);
                    _pageCache.Remove(url);
                }
                else
                {
                    _logger.LogInformation("NavigateToAsync: build cache HIT for {Url}, skipping extraction", url);
                    _lastLoadFetchMethod = FetchMethod.Cached;

                    // Refresh TTL so link-list pages stay cached across revisits
                    if (buildCache.Classification == PageClassification.LinkList)
                    {
                        _pageCache.ApplyLinkListTtl(url);
                    }

                    var page = await _pipeline.RebuildFromBuildCacheAsync(buildCache).ConfigureAwait(false);
                    await CompleteNavigation(page, url, options).ConfigureAwait(false);
                    return;
                }
            }

            var htmlCached = _pageCache.Contains(url);
            _logger.LogInformation("NavigateToAsync: build cache MISS, HTML cache {Result} for {Url}", htmlCached ? "HIT" : "MISS", url);
            if (htmlCached)
            {
                // HTML cached — extraction is fast (especially for LinkList with content skip).
                // Load synchronously to avoid skeleton flash.
                var page = await LoadPageAsync(url, cancellationToken).ConfigureAwait(false);
                await CompleteNavigation(page, url, options).ConfigureAwait(false);
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

            await RenderCurrentPageAsync(options, CancellationToken.None).ConfigureAwait(false);

            PlayDecryptRevealAnimation(page);

            // Eagerly warm up the browser for paywalled or JS-heavy domains
            var warmupBrowserAvailable = (_browserSession as IBrowserSession)?.IsBrowserAvailable ?? false;
            var needsBrowserWarmup = _browserConfig.IsPaywalledDomain(url) || _preloadService.IsDomainNeedsJs(url);
            if (warmupBrowserAvailable && needsBrowserWarmup && _lastLoadFetchMethod != FetchMethod.Browser)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation(
                            "JS-heavy/paywalled domain detected, warming up browser session: {Url}", url);
                        await _browserSession.WarmUpAsync().ConfigureAwait(false);
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
    /// Plays the decrypt-reveal animation on the page title after a new page is rendered.
    /// The title resolves from noise through random letters to the correct text.
    /// Skipped when animations are disabled or the page has no title.
    /// </summary>
    private void PlayDecryptRevealAnimation(Page page)
    {
        if (_browserConfig.DisableAnimations)
        {
            return;
        }

        var title = page.ReadableContent?.Title ?? page.Metadata?.Title;
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        try
        {
            var terminalWidth = Console.WindowWidth;
            var maxTitleWidth = Math.Max(1, (terminalWidth - 2) / 2);
            var displayTitle = UI.Renderers.RenderHelpers.TruncateText(title, maxTitleWidth);

            var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);

            // Title is rendered at row 0, column 1 (after leading space in RenderHeader)
            DecryptRevealAnimation.Play(displayTitle, row: 0, col: 1, palette);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Decrypt reveal animation failed");
        }
    }

    private async Task<bool> HandleCommandAsync(NavigationCommand command, RenderOptions options, CancellationToken cancellationToken)
    {
        _idleDetector.RecordActivity();

        // Auto-commit expired undo states
        await UndoCommandHandler.CommitIfExpired(_commandContext, cancellationToken).ConfigureAwait(false);

        // For any command other than Undo, passive commands, or DeleteItem
        // (which handles its own undo commit), commit pending undo immediately
        if (command.Type is not CommandType.Undo
            and not CommandType.NoOp
            and not CommandType.TerminalResized
            and not CommandType.DeleteItem
            && _commandContext.PendingUndo != null)
        {
            await UndoCommandHandler.ClearOnAction(_commandContext, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Handle launcher-specific commands first
            if (_navigationService.InLauncherMode)
            {
                return await LauncherCommandHandler.Handle(_commandContext, command, options, cancellationToken).ConfigureAwait(false);
            }

            // Layout preview mode: intercept keys for carousel control
            if (_navigationService.IsInPreviewMode)
            {
                switch (command.Type)
                {
                    case CommandType.ExpandNode or CommandType.MoveRight:
                        await LayoutCommandHandler.HandleCycleRight(_commandContext, options, cancellationToken).ConfigureAwait(false);
                        return true;
                    case CommandType.CollapseNode or CommandType.MoveLeft:
                        await LayoutCommandHandler.HandleCycleLeft(_commandContext, options, cancellationToken).ConfigureAwait(false);
                        return true;
                    case CommandType.ActivateLink:
                        await StrategyChooserHandler.HandleApplyAsync(_commandContext, options, cancellationToken).ConfigureAwait(false);
                        return true;
                    case CommandType.GoBack:
                        await LayoutCommandHandler.HandleCancel(_commandContext, options, cancellationToken).ConfigureAwait(false);
                        return true;
                    case CommandType.InteractiveRefresh:
                        // workspace-z5qz: `i` on an AI Curated preview row is
                        // reserved for the upcoming user-guidance prompt
                        // (workspace-99ve). Until that ships, surface a clear
                        // "coming soon" message so the affordance hinted in
                        // the row summary doesn't just no-op silently.
                        await StrategyChooserHandler.HandleGuidanceRequestAsync(_commandContext, options, cancellationToken).ConfigureAwait(false);
                        return true;
                    case CommandType.Quit:
                        return false;
                    case CommandType.TerminalResized:
                        break; // Fall through to normal handling
                    default:
                        return true; // Ignore other keys in preview mode
                }
            }

            var commandType = command.Type;

            switch (commandType)
            {
                case CommandType.Quit:
                    return false;

                case CommandType.MoveDown:
                    await NavigationCommandHandler.HandleMoveDown(_commandContext, command, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.MoveUp:
                    await NavigationCommandHandler.HandleMoveUp(_commandContext, command, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ExpandNode:
                    await NavigationCommandHandler.HandleExpandNode(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.CollapseNode:
                    await NavigationCommandHandler.HandleCollapseNode(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ToggleNode:
                    await NavigationCommandHandler.HandleToggleNode(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ToggleSelection:
                    // Spacebar: toggle speed read in Readable view, toggle selection in Hierarchical
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

                        await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await NavigationCommandHandler.HandleToggleSelection(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case CommandType.ActivateLink:
                    await NavigationCommandHandler.HandleActivateLink(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.GoBack:
                    // workspace-c8v3: when the prefetch detail overlay is up,
                    // Esc/Backspace dismisses the overlay instead of falling
                    // back to history navigation. The overlay is the closest
                    // modal-ish thing so it should consume the gesture first.
                    if (_commandContext.IsPreloadDetailVisible)
                    {
                        _commandContext.IsPreloadDetailVisible = false;
                        var refreshed = GetCurrentRenderOptions();
                        await RenderCurrentPageAsync(refreshed, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    await NavigationCommandHandler.HandleGoBack(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.TogglePreloadDetail:
                    _commandContext.IsPreloadDetailVisible = !_commandContext.IsPreloadDetailVisible;
                    var togglesOptions = GetCurrentRenderOptions();
                    await RenderCurrentPageAsync(togglesOptions, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.GoForward:
                    await NavigationCommandHandler.HandleGoForward(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.PageDown:
                    await NavigationCommandHandler.HandlePageDown(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.PageUp:
                    await NavigationCommandHandler.HandlePageUp(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ParagraphDown:
                    await NavigationCommandHandler.HandleParagraphDown(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ParagraphUp:
                    await NavigationCommandHandler.HandleParagraphUp(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.GoToTop:
                    await NavigationCommandHandler.HandleGoToTop(_commandContext, command, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.GoToBottom:
                    await NavigationCommandHandler.HandleGoToBottom(_commandContext, command, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.Refresh:
                    await NavigationCommandHandler.HandleRefresh(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ForceRefresh:
                    await NavigationCommandHandler.HandleForceRefresh(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.InteractiveRefresh:
                    await NavigationCommandHandler.HandleInteractiveRefresh(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.Navigate:
                    await NavigationCommandHandler.HandleNavigate(_commandContext, command, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.SwitchView:
                    await ViewCommandHandler.HandleSwitchView(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.SwitchToHierarchical:
                    await ViewCommandHandler.HandleSwitchToHierarchical(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.SwitchToReadable:
                    await ViewCommandHandler.HandleSwitchToReadable(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ShowHelp:
                    await ViewCommandHandler.HandleShowHelp(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.IncreaseWidth:
                    await ViewCommandHandler.HandleIncreaseWidth(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.DecreaseWidth:
                    await ViewCommandHandler.HandleDecreaseWidth(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ResetWidth:
                    await ViewCommandHandler.HandleResetWidth(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.OpenLauncher:
                    await ViewCommandHandler.HandleOpenLauncher(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.CycleTheme:
                    await ViewCommandHandler.HandleCycleTheme(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.TerminalResized:
                    await ViewCommandHandler.HandleTerminalResized(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.OpenCommandLine:
                    if (!await SearchCommandHandler.HandleOpenCommandLine(_commandContext, options, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }

                    break;
                case CommandType.Search:
                    await SearchCommandHandler.HandleSearch(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.SearchNext:
                    await SearchCommandHandler.HandleSearchNext(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.SearchPrevious:
                    await SearchCommandHandler.HandleSearchPrevious(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.SaveToCollection:
                    await CollectionCommandHandler.HandleSaveToCollection(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.SaveToSpecific:
                    await CollectionCommandHandler.HandleSaveToSpecific(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.SaveAllToReadingList:
                    await CollectionCommandHandler.HandleSaveAllToReadingList(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.OpenCollections:
                    await CollectionCommandHandler.HandleOpenCollections(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.DeleteItem:
                    await CollectionCommandHandler.HandleDeleteItem(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ReorderUp:
                    await CollectionCommandHandler.HandleReorderUp(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ReorderDown:
                    await CollectionCommandHandler.HandleReorderDown(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.ClearCollection:
                    await CollectionCommandHandler.HandleClearCollection(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.GeneratePodcast:
                    await PodcastCommandHandler.HandleGeneratePodcast(
                        _commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.RestorePodcastModal:
                    // workspace-vkhr Phase D: Shift+P restores the detached
                    // podcast modal when a background job is active. No-op
                    // (with a brief status message) when none is running.
                    await PodcastCommandHandler.HandleRestorePodcastModal(
                        _commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.ChooseLayout:
                    await StrategyChooserHandler.HandleOpenChooserAsync(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.DumpHtml:
                    await ViewCommandHandler.HandleDumpHtml(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.OpenInBrowser:
                    await ViewCommandHandler.HandleOpenInBrowser(_commandContext, options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.AddBookmark:
                    // Only handle in launcher mode (handled above), ignore in other views
                    break;

                case CommandType.Undo:
                    await UndoCommandHandler.HandleUndo(_commandContext, options, cancellationToken).ConfigureAwait(false);
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

                        await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case CommandType.SpeedReadFaster:
                    _navigationService.AdjustSpeedReadWpm(25);
                    _navigationService.SetStatusMessage($"{_navigationService.SpeedReadWpm} WPM");
                    await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandType.SpeedReadSlower:
                    _navigationService.AdjustSpeedReadWpm(-25);
                    _navigationService.SetStatusMessage($"{_navigationService.SpeedReadWpm} WPM");
                    await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                    break;

                case CommandType.RegenerateArticleLayout:
                    await ArticleLayoutCommandHandler.HandleRegenerateAsync(_commandContext, options, cancellationToken).ConfigureAwait(false);
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
            // Auto-dismiss non-sticky toasts that were already shown once
            _navigationService.MarkToastRendered();

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

            await RenderAsync(page, viewMode, options, cancellationToken).ConfigureAwait(false);
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
    /// workspace-vkhr Phase D acceptance criterion ("on completion the modal
    /// auto-pops with the result screen"): when a detached podcast job has
    /// finished, hand control to <see cref="PodcastCommandHandler.HandleRestorePodcastModal"/>
    /// which renders the completion / error screen and calls
    /// <see cref="Application.Interfaces.Browser.IPodcastBackgroundJobManager.Clear"/>.
    /// Returns true when a completion was surfaced (the main loop must
    /// <c>continue</c> so the next iteration sees fresh terminal state).
    /// </summary>
    /// <remarks>
    /// Only triggers from the top-level input loop, which is unreachable
    /// while any of the podcast modal flows are on screen — so this never
    /// double-fires for an attached user.
    /// </remarks>
    private async Task<bool> CheckDetachedPodcastCompletionAsync(
        RenderOptions options,
        CancellationToken cancellationToken)
    {
        Application.Interfaces.Browser.IPodcastBackgroundJobManager? jobManager;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            jobManager = scope.ServiceProvider
                .GetService<Application.Interfaces.Browser.IPodcastBackgroundJobManager>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve podcast background job manager");
            return false;
        }

        if (jobManager is null
            || !jobManager.HasActiveJob
            || jobManager.CurrentJobTask is null
            || !jobManager.CurrentJobTask.IsCompleted)
        {
            return false;
        }

        await CommandHandlers.PodcastCommandHandler.HandleRestorePodcastModal(
            _commandContext, options, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Returns true when at least one of the four primary credentials is still
    /// unconfigured (workspace-9qzh). Drives the launcher's Setup hint
    /// visibility AND the `S`-key intercept's gating, so partial-setup users
    /// can still discover and reach Setup.
    /// </summary>
    private bool HasIncompleteSetup()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<Application.Interfaces.IUserSettingsStore>();
            return CommandHandlers.SettingsCommandHandler.HasIncompleteSetup(settingsStore);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Incomplete-setup detection failed; treating as fully configured");
            return false;
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
            await RefreshBookmarksAsync(cancellationToken).ConfigureAwait(false);
            await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enter launcher mode");
        }
    }

    /// <summary>
    /// Refreshes the cached bookmarks from the database, reconciling against
    /// the user's bookmarks.json config file (creating it from shipped defaults
    /// or DB on first run, and additively merging new shipped defaults).
    /// </summary>
    private async Task RefreshBookmarksAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
            await bookmarkService.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            var all = await bookmarkService.GetAllBookmarksAsync(cancellationToken).ConfigureAwait(false);
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
                TimeSpan.FromHours(16), cancellationToken).ConfigureAwait(false);

            var allCollections = await collectionService.GetAllCollectionsAsync(cancellationToken).ConfigureAwait(false);
            _commandContext.Collections = allCollections.ToList();

            var defaultCollection = await collectionService.GetDefaultCollectionAsync(cancellationToken).ConfigureAwait(false);
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
