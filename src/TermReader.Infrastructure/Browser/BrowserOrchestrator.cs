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
    private readonly IBrowserSession _browserSession;
    private readonly IThemeProvider _themeProvider;
    private readonly ILogger<BrowserOrchestrator> _logger;

    // Line cache for reader view line-based scrolling
    private List<string>? _cachedLines;
    private int _cachedWidth;

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
        IBrowserSession browserSession,
        IThemeProvider themeProvider,
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
        _logger = logger;
    }

    public async Task<Page> LoadPageAsync(string url, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading page: {Url}", url);
        _logger.LogDebug("BrowserConfig.Headless = {Headless}", _browserConfig.Headless);
        _renderer.RenderLoading(url);

        // Load the page HTML
        var loadResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = url, Headless = _browserConfig.Headless },
            cancellationToken);

        if (!loadResult.Success)
        {
            throw new InvalidOperationException($"Failed to load page: {loadResult.ErrorMessage}");
        }

        // Create page entity
        var metadata = loadResult.Metadata ?? new PageMetadata { Title = "Untitled" };
        var page = Page.Create(loadResult.Url, loadResult.Html, metadata);

        // Extract links and build navigation tree
        var links = await _linkExtractor.ExtractLinksAsync(loadResult.Html, loadResult.Url, cancellationToken);
        var tree = await _treeBuilder.BuildTreeAsync(links, cancellationToken);
        page.SetLinkTree(tree);

        // Try to extract readable content
        var readable = await _contentExtractor.ExtractAsync(loadResult.Html, loadResult.Url, cancellationToken);
        if (readable != null)
        {
            page.SetReadableContent(readable);
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
                EnsureLineCache(options);
                _renderer.RenderReadable(page, context, options, _cachedLines);
                break;

            case ViewMode.CollectionList:
                _renderer.RenderCollectionList(
                    _collections ?? new List<Collection>(),
                    _navigationService.CollectionSelectedIndex,
                    _defaultCollectionId,
                    options);
                break;

            case ViewMode.CollectionItems:
                var activeCollection = _navigationService.ActiveCollection;
                if (activeCollection != null)
                {
                    _renderer.RenderCollectionItems(
                        activeCollection,
                        _navigationService.CollectionItemSelectedIndex,
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
            // Dispose browser session (defense-in-depth alongside host disposal)
            try
            {
                _browserSession.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing browser session during shutdown");
            }

            // Exit alternate screen buffer and restore cursor
            Console.Write("\x1b[?1049l");
            Console.CursorVisible = true;
        }
    }

    /// <summary>
    /// Returns the actual usable viewport height for hierarchical view.
    /// Accounts for header (6 lines: blank + box top + title + url + box bottom + blank)
    /// and status bar area (3 lines: blank + separator + status text).
    /// </summary>
    private static int GetHierarchicalViewportHeight(RenderOptions options)
    {
        return Math.Max(3, options.TerminalHeight - 9);
    }

    /// <summary>
    /// Calculates the available viewport height for the reader view,
    /// accounting for the article header and status bar.
    /// Header is ~8 lines (border + title + url + border + blank + metadata + blank),
    /// status bar is ~3 lines (blank + separator + status text).
    /// </summary>
    private static int GetReaderViewportHeight(RenderOptions options)
    {
        // Header ~8 lines, status bar ~3 lines
        return Math.Max(3, options.TerminalHeight - 11);
    }

    /// <summary>
    /// Pre-wraps all paragraphs into a flat list of display lines for the reader view.
    /// </summary>
    private static List<string> WrapAllContent(ReadableContent content, int maxWidth)
    {
        var allLines = new List<string>();
        foreach (var paragraph in content.Paragraphs)
        {
            var wrapped = UI.Renderers.RenderHelpers.WrapText(paragraph, maxWidth - 4);
            foreach (var line in wrapped)
            {
                allLines.Add($"  {line}");
            }

            allLines.Add(string.Empty);
        }

        return allLines;
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
                ? Math.Clamp(_contentWidthOverride.Value, MinContentWidth, MaxContentWidth)
                : Math.Clamp(width - 2, MinContentWidth, MaxContentWidth),
            Use256Colors = use256
        };
    }

    private async Task NavigateToAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var page = await LoadPageAsync(url, cancellationToken);
            _navigationService.NavigateTo(page);
            InvalidateLineCache();
            await RenderCurrentPageAsync(options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error navigating to {Url}", url);
            _renderer.RenderError(ex.Message, url);
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
            NavigateToAsync = NavigateToAsync,
            RenderCurrentPageAsync = RenderCurrentPageAsync,
            RefreshCollectionsAsync = RefreshCollectionsAsync,
            RefreshBookmarksAsync = RefreshBookmarksAsync,
            GetCurrentRenderOptions = GetCurrentRenderOptions,
            CreateCollectionService = CreateCollectionService,
            InvalidateLineCache = InvalidateLineCache,
            EnsureLineCache = EnsureLineCache,
            GetReaderViewportHeight = GetReaderViewportHeight,
            GetHierarchicalViewportHeight = GetHierarchicalViewportHeight,
            AdjustScrollForSelection = AdjustScrollForSelection,
            ScrollToSearchMatch = ScrollToSearchMatch,
            PreserveScrollPositionAfterRewrap = PreserveScrollPositionAfterRewrap,
        };

        // Sync mutable state
        _commandContext.Collections = _collections;
        _commandContext.DefaultCollectionId = _defaultCollectionId;
        _commandContext.Bookmarks = _bookmarks;
        _commandContext.CachedLines = _cachedLines;
        _commandContext.CachedWidth = _cachedWidth;
        _commandContext.ContentWidthOverride = _contentWidthOverride;

        return _commandContext;
    }

    private void SyncFromCommandContext()
    {
        if (_commandContext == null)
        {
            return;
        }

        _collections = _commandContext.Collections;
        _defaultCollectionId = _commandContext.DefaultCollectionId;
        _bookmarks = _commandContext.Bookmarks;
        _cachedLines = _commandContext.CachedLines;
        _cachedWidth = _commandContext.CachedWidth;
        _contentWidthOverride = _commandContext.ContentWidthOverride;
    }

    private async Task<bool> HandleCommandAsync(NavigationCommand command, RenderOptions options, CancellationToken cancellationToken)
    {
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
                    await RenderCurrentPageAsync(options, cancellationToken);
                    break;

                case CommandType.AddBookmark:
                    // Only handle in launcher mode (handled above), ignore in other views
                    break;
            }

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
            EnsureLineCache(options);

            if (_cachedLines != null)
            {
                // Search in pre-wrapped lines for precise line-based scrolling
                var matches = new List<int>();
                for (var i = 0; i < _cachedLines.Count; i++)
                {
                    if (_cachedLines[i].Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
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

        var currentOffset = _navigationService.CurrentContext.ScrollOffset;
        var contentHeight = GetHierarchicalViewportHeight(options);

        // If selection is above visible area, scroll up
        if (selectedIndex < currentOffset)
        {
            _navigationService.SetScrollOffset(selectedIndex);
        }

        // If selection is below visible area, scroll down
        else if (selectedIndex >= currentOffset + contentHeight)
        {
            _navigationService.SetScrollOffset(selectedIndex - contentHeight + 1);
        }
    }

    /// <summary>
    /// Ensures the line cache is populated and matches the current content width.
    /// Rebuilds if width changed or cache is empty.
    /// </summary>
    private void EnsureLineCache(RenderOptions options)
    {
        var contentWidth = options.MaxContentWidth;
        if (_cachedLines != null && _cachedWidth == contentWidth)
        {
            return;
        }

        var page = _navigationService.CurrentPage;
        if (page?.ReadableContent == null)
        {
            _cachedLines = null;
            return;
        }

        _cachedLines = WrapAllContent(page.ReadableContent, contentWidth);
        _cachedWidth = contentWidth;
    }

    /// <summary>
    /// Invalidates the line cache so it is rebuilt on next access.
    /// </summary>
    private void InvalidateLineCache()
    {
        _cachedLines = null;
        _cachedWidth = 0;
    }

    /// <summary>
    /// Preserves the reading position when content width changes by computing
    /// the character offset of the current scroll position in the old lines,
    /// re-wrapping with the new width, and finding the matching line index.
    /// </summary>
    private void PreserveScrollPositionAfterRewrap(RenderOptions newOptions)
    {
        var page = _navigationService.CurrentPage;
        if (page?.ReadableContent == null || _cachedLines == null || _cachedLines.Count == 0)
        {
            InvalidateLineCache();
            return;
        }

        var currentScroll = _navigationService.CurrentContext.ScrollOffset;

        // Count character offset up to the current scroll line
        var charOffset = 0;
        for (var i = 0; i < Math.Min(currentScroll, _cachedLines.Count); i++)
        {
            charOffset += _cachedLines[i].TrimStart().Length + 1; // +1 for implicit newline
        }

        // Invalidate and rebuild with new width
        InvalidateLineCache();
        EnsureLineCache(newOptions);

        if (_cachedLines == null || _cachedLines.Count == 0)
        {
            return;
        }

        // Find the line index in new lines matching the character offset
        var accumulatedChars = 0;
        var newLineIndex = 0;
        for (var i = 0; i < _cachedLines.Count; i++)
        {
            accumulatedChars += _cachedLines[i].TrimStart().Length + 1;
            if (accumulatedChars >= charOffset)
            {
                newLineIndex = i;
                break;
            }

            newLineIndex = i;
        }

        _navigationService.SetScrollOffset(Math.Clamp(newLineIndex, 0, Math.Max(0, _cachedLines.Count - 1)));
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
                var updatedActive = _collections.Find(c => c.Id == _navigationService.ActiveCollection.Id);
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
