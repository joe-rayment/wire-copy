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
    private readonly ILogger<BrowserOrchestrator> _logger;
    private readonly LineCacheManager _lineCacheManager;

    // Tracks FetchMethod from the last LoadPageAsync call for NavigateToAsync
    private FetchMethod _lastLoadFetchMethod;

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
        _logger = logger;
        _lineCacheManager = new LineCacheManager(navigationService, themeProvider);

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

        if (!_pageCache.Contains(url))
        {
            _renderer.RenderLoading(url);
        }

        // Load the page HTML
        var loadResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, Headless = _browserConfig.Headless },
            cancellationToken);

        if (!loadResult.Success)
        {
            throw new InvalidOperationException($"Failed to load page: {loadResult.ErrorMessage}");
        }

        _lastLoadFetchMethod = loadResult.FetchMethod;

        var page = await BuildPageFromLoadResultAsync(loadResult, cancellationToken);

        // Bot challenge handling: if Selenium returned a challenge page in headed mode,
        // wait for the user to solve it in the visible browser window
        var challengeResult = await HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken);
        if (challengeResult != null)
        {
            _lastLoadFetchMethod = challengeResult.FetchMethod;
            page = await BuildPageFromLoadResultAsync(challengeResult, cancellationToken);
        }

        // Content-quality fallback: if no content from HTTP/cached page, retry with Selenium
        if (!page.HasReadableContent() && loadResult.FetchMethod != FetchMethod.Selenium)
        {
            _logger.LogInformation(
                "No readable content from {FetchMethod} page, retrying with Selenium: {Url}",
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
                page = await BuildPageFromLoadResultAsync(retryResult, cancellationToken);
            }
        }

        // Headless challenge fallback: if headless Selenium got a bot challenge,
        // retry in headed mode where DataDome is less likely to block
        if (!page.HasReadableContent() &&
            loadResult.FetchMethod == FetchMethod.Selenium &&
            _browserConfig.Headless &&
            PageLoader.IsBotChallengePage(loadResult.Html))
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
                page = await BuildPageFromLoadResultAsync(headedResult, cancellationToken);
            }
        }

        _logger.LogInformation("Page loaded: {Title} - {LinkCount} links, {HasReadable} readable",
            page.Metadata.Title,
            page.LinkTree?.TotalLinks ?? 0,
            page.HasReadableContent() ? "has" : "no");

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
            _ = _resizeDetector.StartAsync(cancellationToken);
            _ = _preloadService.StartAsync(cancellationToken);

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

            // Main input loop
            while (!cancellationToken.IsCancellationRequested)
            {
                // Re-read terminal dimensions on every iteration to handle resize
                options = GetCurrentRenderOptions();

                var command = await _inputHandler.WaitForInputAsync(cancellationToken);
                var shouldContinue = await HandleCommandAsync(command, options, cancellationToken);

                if (!shouldContinue)
                {
                    break;
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
            _renderer.RenderError(ex.Message, _navigationService.CurrentPage?.Url ?? "unknown");
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
    private static int GetHierarchicalViewportHeight(RenderOptions options)
    {
        var layout = UI.Renderers.LinkTreeRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        return layout.VisibleRows;
    }

    /// <summary>
    /// Calculates the available viewport height for the reader view.
    /// The headline is now embedded in the scrollable line cache,
    /// so only the status bar area (3 lines) is reserved.
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
            MaxContentWidth = _commandContext.ContentWidthOverride.HasValue
                ? Math.Clamp(Math.Min(_commandContext.ContentWidthOverride.Value, width - 2), Math.Min(MinContentWidth, width - 2), MaxContentWidth)
                : Math.Clamp(width - 2, Math.Min(MinContentWidth, width - 2), MaxContentWidth),
            Use256Colors = use256,
            CachedUrls = _pageCache.GetCachedUrls(),
            CacheProgress = _preloadService.GetProgress()
        };
    }

    /// <summary>
    /// Builds a Page entity from a PageLoadResult, extracting links, tree, and readable content.
    /// </summary>
    private async Task<Page> BuildPageFromLoadResultAsync(PageLoadResult loadResult, CancellationToken cancellationToken)
    {
        var metadata = loadResult.Metadata ?? new PageMetadata { Title = "Untitled" };
        var page = Page.Create(loadResult.Url, loadResult.Html, metadata);

        var links = await _linkExtractor.ExtractLinksAsync(loadResult.Html, loadResult.Url, cancellationToken);
        var tree = await _treeBuilder.BuildTreeAsync(links, cancellationToken);
        page.SetLinkTree(tree);

        var readable = await _contentExtractor.ExtractAsync(loadResult.Html, loadResult.Url, cancellationToken);
        if (readable != null)
        {
            page.SetReadableContent(readable);
        }

        return page;
    }

    private async Task NavigateToAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _preloadService.Pause();

        try
        {
            var page = await LoadPageAsync(url, cancellationToken);
            _navigationService.NavigateTo(page);

            // Derive cache info from actual FetchMethod (avoids race with cache expiry)
            var isFromCache = _lastLoadFetchMethod == FetchMethod.Cached;
            var cachedAt = isFromCache ? _pageCache.GetCachedAt(url) : null;
            _navigationService.SetCacheInfo(isFromCache, cachedAt);
            _lineCacheManager.InvalidateLineCache();

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error navigating to {Url}", url);
            _renderer.RenderError(ex.Message, url);
        }
        finally
        {
            _preloadService.Resume();
        }
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

            var page = await BuildPageFromLoadResultAsync(loadResult, cancellationToken);

            _navigationService.NavigateTo(page);
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
            _renderer.RenderInteractiveRefresh(url);

            // Force headed mode for interactive refresh
            var loadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = false, ForceRefresh = true },
                cancellationToken);

            if (!loadResult.Success)
            {
                _renderer.RenderError($"Failed to load page: {loadResult.ErrorMessage}", url);
                return;
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
                // User pressed Esc — cancel
                await RenderCurrentPageAsync(options, cancellationToken);
                return;
            }

            // Accept: build page, cache, and re-render
            var page = await BuildPageFromLoadResultAsync(loadResult, cancellationToken);

            _pageCache.Put(url, loadResult);
            _navigationService.NavigateTo(page);
            _navigationService.SetCacheInfo(false, null);
            _lineCacheManager.InvalidateLineCache();

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

        if (loadResult.FetchMethod != FetchMethod.Selenium ||
            !PageLoader.IsBotChallengePage(loadResult.Html) ||
            headless)
        {
            return null;
        }

        _logger.LogWarning("Bot challenge detected in headed mode, waiting for user to resolve: {Url}", url);
        _renderer.RenderChallenge(url);

        var challengeTimeout = TimeSpan.FromMinutes(2);
        var sw = Stopwatch.StartNew();
        var resolved = false;

        while (sw.Elapsed < challengeTimeout && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1500, cancellationToken);
            try
            {
                if (_browserSession is IBrowserSession session)
                {
                    var driver = session.GetOrCreateDriver(headless);
                    var currentSource = driver.PageSource;
                    if (!PageLoader.IsBotChallengePage(currentSource))
                    {
                        _logger.LogInformation("Bot challenge resolved by user: {Url}", url);
                        resolved = true;
                        break;
                    }
                }
                else
                {
                    _logger.LogDebug("Browser session does not support driver access, skipping challenge poll");
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Error polling challenge status");
                break;
            }
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

    private async Task<bool> HandleCommandAsync(NavigationCommand command, RenderOptions options, CancellationToken cancellationToken)
    {
        _idleDetector.RecordActivity();

        // Handle launcher-specific commands first
        if (_navigationService.InLauncherMode)
        {
            return await LauncherCommandHandler.Handle(_commandContext, command, options, cancellationToken);
        }

        // Remap h/l (CollapseNode/ExpandNode) to width controls in Reader View
        var commandType = RemapForViewMode(command.Type);

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
                await SearchCommandHandler.HandleOpenCommandLine(_commandContext, options, cancellationToken);
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

            case CommandType.GeneratePodcast:
                // Delegates to PodcastCommandHandler (created in next task)
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    _navigationService.SetStatusMessage("Podcast generation not yet available");
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                break;

            case CommandType.AddBookmark:
                // Only handle in launcher mode (handled above), ignore in other views
                break;
        }

        // Notify pre-loader of selection changes in hierarchical view
        NotifyPreloadSelectionChanged();

        return true;
    }

    private CommandType RemapForViewMode(CommandType commandType)
    {
        if (_navigationService.CurrentContext.ViewMode != ViewMode.Readable)
        {
            return commandType;
        }

        return commandType switch
        {
            CommandType.CollapseNode => CommandType.DecreaseWidth,
            CommandType.ExpandNode => CommandType.IncreaseWidth,
            _ => commandType,
        };
    }

    private async Task RenderCurrentPageAsync(RenderOptions options, CancellationToken cancellationToken)
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
            _navigationService.SetScrollOffset(gridRow);
        }
        else if (gridRow >= currentOffset + contentHeight)
        {
            _navigationService.SetScrollOffset(gridRow - contentHeight + 1);
        }
    }

    /// <summary>
    /// Notifies the pre-load service of the current selection in the hierarchical view.
    /// </summary>
    private void NotifyPreloadSelectionChanged()
    {
        if (_navigationService.CurrentContext.ViewMode != ViewMode.Hierarchical)
        {
            return;
        }

        var page = _navigationService.CurrentPage;
        var tree = page?.LinkTree;
        if (tree == null)
        {
            return;
        }

        var visibleNodes = tree.GetVisibleNodes().ToList();
        var selectedNode = tree.CurrentSelection;
        var selectedIndex = selectedNode != null ? visibleNodes.IndexOf(selectedNode) : 0;

        _preloadService.NotifySelectionChanged(
            Math.Max(0, selectedIndex),
            visibleNodes,
            page!.Url);
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
                    _navigationService.EnterCollection(updatedActive);
                    var maxIndex = Math.Max(0, updatedActive.Items.Count - 1);
                    _navigationService.CollectionItemSelectedIndex = Math.Min(savedItemIndex, maxIndex);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh collections");
        }
    }
}
