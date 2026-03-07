// Educational and personal use only.

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
using TermReader.Infrastructure.Browser.Themes;

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
    private readonly IThemeProvider _themeProvider;
    private readonly IResizeDetector _resizeDetector;
    private readonly IPageCache _pageCache;
    private readonly IPreloadService _preloadService;
    private readonly IIdleDetector _idleDetector;
    private readonly ILogger<BrowserOrchestrator> _logger;
    private readonly LineCacheManager _lineCacheManager;

    // Tracks FetchMethod from the last LoadPageAsync call for NavigateToAsync
    private FetchMethod _lastLoadFetchMethod;

    // Collections state
    private List<Collection>? _collections;
    private Guid? _defaultCollectionId;

    // Launcher state
    private List<Domain.Entities.Bookmarks.Bookmark>? _bookmarks;

    // Content width override for reader view
    private int? _contentWidthOverride;

    // Command handler context (lazily initialized)
    private CommandContext? _commandContext;

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
        _themeProvider = themeProvider;
        _resizeDetector = resizeDetector;
        _pageCache = pageCache;
        _preloadService = preloadService;
        _idleDetector = idleDetector;
        _logger = logger;
        _lineCacheManager = new LineCacheManager(navigationService, themeProvider);
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
                    _collections ?? new List<Collection>(),
                    _navigationService.CollectionSelectedIndex,
                    _defaultCollectionId,
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
                    _bookmarks?.ToList() ?? new List<Domain.Entities.Bookmarks.Bookmark>(),
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
            MaxContentWidth = _contentWidthOverride.HasValue
                ? Math.Clamp(Math.Min(_contentWidthOverride.Value, width - 2), Math.Min(MinContentWidth, width - 2), MaxContentWidth)
                : Math.Clamp(width - 2, Math.Min(MinContentWidth, width - 2), MaxContentWidth),
            Use256Colors = use256,
            CachedUrls = _pageCache.GetCachedUrls()
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

    private CommandContext GetCommandContext()
    {
        _commandContext ??= new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = _renderer,
            InputHandler = _inputHandler,
            ScopeFactory = _scopeFactory,
            Logger = _logger,
            PageCache = _pageCache,
            LineCacheManager = _lineCacheManager,
            NavigateToAsync = NavigateToAsync,
            ForceRefreshAsync = ForceRefreshAsync,
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

        // Sync mutable state
        _commandContext.Collections = _collections;
        _commandContext.DefaultCollectionId = _defaultCollectionId;
        _commandContext.Bookmarks = _bookmarks;
        _commandContext.ContentWidthOverride = _contentWidthOverride;

        return _commandContext;
    }

    private void SyncFromCommandContext()
    {
        if (_commandContext == null)
        {
            return;
        }

        _collections = _commandContext.Collections as List<Collection> ?? _commandContext.Collections?.ToList();
        _defaultCollectionId = _commandContext.DefaultCollectionId;
        _bookmarks = _commandContext.Bookmarks as List<Domain.Entities.Bookmarks.Bookmark>
            ?? _commandContext.Bookmarks?.ToList();
        _contentWidthOverride = _commandContext.ContentWidthOverride;
    }

    private async Task<bool> HandleCommandAsync(NavigationCommand command, RenderOptions options, CancellationToken cancellationToken)
    {
        _idleDetector.RecordActivity();
        var ctx = GetCommandContext();

        try
        {
            // Handle launcher-specific commands first
            if (_navigationService.InLauncherMode)
            {
                return await LauncherCommandHandler.Handle(ctx, command, options, cancellationToken);
            }

            switch (command.Type)
            {
                case CommandType.Quit:
                    return false;

                case CommandType.MoveDown:
                    await NavigationCommandHandler.HandleMoveDown(ctx, options, cancellationToken);
                    break;
                case CommandType.MoveUp:
                    await NavigationCommandHandler.HandleMoveUp(ctx, options, cancellationToken);
                    break;
                case CommandType.ExpandNode:
                    await NavigationCommandHandler.HandleExpandNode(ctx, options, cancellationToken);
                    break;
                case CommandType.CollapseNode:
                    await NavigationCommandHandler.HandleCollapseNode(ctx, options, cancellationToken);
                    break;
                case CommandType.ToggleNode:
                    await NavigationCommandHandler.HandleToggleNode(ctx, options, cancellationToken);
                    break;
                case CommandType.ActivateLink:
                    await NavigationCommandHandler.HandleActivateLink(ctx, options, cancellationToken);
                    break;
                case CommandType.GoBack:
                    await NavigationCommandHandler.HandleGoBack(ctx, options, cancellationToken);
                    break;
                case CommandType.GoForward:
                    await NavigationCommandHandler.HandleGoForward(ctx, options, cancellationToken);
                    break;
                case CommandType.PageDown:
                    await NavigationCommandHandler.HandlePageDown(ctx, options, cancellationToken);
                    break;
                case CommandType.PageUp:
                    await NavigationCommandHandler.HandlePageUp(ctx, options, cancellationToken);
                    break;
                case CommandType.GoToTop:
                    await NavigationCommandHandler.HandleGoToTop(ctx, options, cancellationToken);
                    break;
                case CommandType.GoToBottom:
                    await NavigationCommandHandler.HandleGoToBottom(ctx, options, cancellationToken);
                    break;
                case CommandType.Refresh:
                    await NavigationCommandHandler.HandleRefresh(ctx, options, cancellationToken);
                    break;
                case CommandType.ForceRefresh:
                    await NavigationCommandHandler.HandleForceRefresh(ctx, options, cancellationToken);
                    break;
                case CommandType.Navigate:
                    await NavigationCommandHandler.HandleNavigate(ctx, command, options, cancellationToken);
                    break;

                case CommandType.SwitchView:
                    await ViewCommandHandler.HandleSwitchView(ctx, options, cancellationToken);
                    break;
                case CommandType.SwitchToHierarchical:
                    await ViewCommandHandler.HandleSwitchToHierarchical(ctx, options, cancellationToken);
                    break;
                case CommandType.SwitchToReadable:
                    await ViewCommandHandler.HandleSwitchToReadable(ctx, options, cancellationToken);
                    break;
                case CommandType.ShowHelp:
                    await ViewCommandHandler.HandleShowHelp(ctx, options, cancellationToken);
                    break;
                case CommandType.IncreaseWidth:
                    await ViewCommandHandler.HandleIncreaseWidth(ctx, options, cancellationToken);
                    break;
                case CommandType.DecreaseWidth:
                    await ViewCommandHandler.HandleDecreaseWidth(ctx, options, cancellationToken);
                    break;
                case CommandType.ResetWidth:
                    await ViewCommandHandler.HandleResetWidth(ctx, options, cancellationToken);
                    break;

                case CommandType.OpenCommandLine:
                    await SearchCommandHandler.HandleOpenCommandLine(ctx, options, cancellationToken);
                    break;
                case CommandType.Search:
                    await SearchCommandHandler.HandleSearch(ctx, options, cancellationToken);
                    break;
                case CommandType.SearchNext:
                    await SearchCommandHandler.HandleSearchNext(ctx, options, cancellationToken);
                    break;
                case CommandType.SearchPrevious:
                    await SearchCommandHandler.HandleSearchPrevious(ctx, options, cancellationToken);
                    break;

                case CommandType.SaveToCollection:
                    await CollectionCommandHandler.HandleSaveToCollection(ctx, options, cancellationToken);
                    break;
                case CommandType.SaveToSpecific:
                    await CollectionCommandHandler.HandleSaveToSpecific(ctx, options, cancellationToken);
                    break;
                case CommandType.SaveAllToReadingList:
                    await CollectionCommandHandler.HandleSaveAllToReadingList(ctx, options, cancellationToken);
                    break;
                case CommandType.OpenCollections:
                    await CollectionCommandHandler.HandleOpenCollections(ctx, options, cancellationToken);
                    break;
                case CommandType.DeleteItem:
                    await CollectionCommandHandler.HandleDeleteItem(ctx, options, cancellationToken);
                    break;
                case CommandType.ReorderUp:
                    await CollectionCommandHandler.HandleReorderUp(ctx, options, cancellationToken);
                    break;
                case CommandType.ReorderDown:
                    await CollectionCommandHandler.HandleReorderDown(ctx, options, cancellationToken);
                    break;

                case CommandType.OpenLauncher:
                    _navigationService.EnterLauncher();
                    await RefreshBookmarksAsync(cancellationToken);
                    await RenderCurrentPageAsync(options, cancellationToken);
                    break;

                case CommandType.CycleTheme:
                    _themeProvider.CycleTheme();
                    _navigationService.SetStatusMessage(_themeProvider.CurrentTheme.ToString());
                    _lineCacheManager.InvalidateLineCache();
                    await RenderCurrentPageAsync(options, cancellationToken);
                    break;

                case CommandType.AddBookmark:
                    // Only handle in launcher mode (handled above), ignore in other views
                    break;

                case CommandType.TerminalResized:
                    var newOptions = GetCurrentRenderOptions();
                    if (_lineCacheManager.CachedWidth > 0 && newOptions.MaxContentWidth != _lineCacheManager.CachedWidth)
                    {
                        _lineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
                    }

                    _lineCacheManager.ClampScrollOffset();
                    await RenderCurrentPageAsync(newOptions, cancellationToken);
                    break;
            }

            // Notify pre-loader of selection changes in hierarchical view
            NotifyPreloadSelectionChanged();

            return true;
        }
        finally
        {
            SyncFromCommandContext();
        }
    }

    private async Task RenderCurrentPageAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        var viewMode = _navigationService.CurrentContext.ViewMode;

        // Launcher view doesn't require a page - render directly
        if (viewMode == ViewMode.Launcher)
        {
            _renderer.RenderLauncher(
                _bookmarks?.ToList() ?? new List<Domain.Entities.Bookmarks.Bookmark>(),
                _navigationService.LauncherSelectedIndex,
                _navigationService.LauncherScrollOffset,
                options);
            return;
        }

        // Collection views don't require a page - render directly
        if (viewMode == ViewMode.CollectionList)
        {
            _renderer.RenderCollectionList(
                _collections ?? new List<Collection>(),
                _navigationService.CollectionSelectedIndex,
                _defaultCollectionId,
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
            _bookmarks = all.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh bookmarks");
            _bookmarks ??= new List<Domain.Entities.Bookmarks.Bookmark>();
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
            _collections = allCollections.ToList();

            var defaultCollection = await collectionService.GetDefaultCollectionAsync(cancellationToken);
            _defaultCollectionId = defaultCollection.Id;

            // Update active collection reference if we're viewing one
            if (_navigationService.ActiveCollection != null)
            {
                var savedItemIndex = _navigationService.CollectionItemSelectedIndex;
                var updatedActive = _collections.FirstOrDefault(c => c.Id == _navigationService.ActiveCollection.Id);
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
