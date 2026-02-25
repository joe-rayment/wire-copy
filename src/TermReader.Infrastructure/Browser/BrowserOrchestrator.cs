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

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Orchestrates the terminal browser functionality.
/// Coordinates page loading, navigation, rendering, and input handling.
/// </summary>
public class BrowserOrchestrator : IBrowserService
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly IInputHandler _inputHandler;
    private readonly NavigationService _navigationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Configuration.BrowserConfiguration _browserConfig;
    private readonly ILogger<BrowserOrchestrator> _logger;

    // Line cache for reader view line-based scrolling
    private List<string>? _cachedLines;
    private int _cachedWidth;

    // Collections state
    private List<Collection>? _collections;
    private Guid? _defaultCollectionId;

    public BrowserOrchestrator(
        IPageLoader pageLoader,
        ILinkExtractor linkExtractor,
        INavigationTreeBuilder treeBuilder,
        IReadableContentExtractor contentExtractor,
        IPageRenderer renderer,
        IInputHandler inputHandler,
        NavigationService navigationService,
        IServiceScopeFactory scopeFactory,
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
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Main browser loop. Handles user input and navigation.
    /// Uses alternate screen buffer for clean TUI experience.
    /// </summary>
    public async Task RunAsync(string? initialUrl = null, CancellationToken cancellationToken = default)
    {
        // Handle SIGINT gracefully: restore main screen buffer before exit
        Console.CancelKeyPress += (_, e) =>
        {
            Console.Write("\x1b[?1049l");
            Console.CursorVisible = true;
            e.Cancel = false;
        };

        try
        {
            // Enter alternate screen buffer and hide cursor
            Console.Write("\x1b[?1049h");
            Console.CursorVisible = false;

            // Get initial URL if not provided
            var url = initialUrl ?? await _inputHandler.PromptForUrlAsync(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogInformation("No URL provided, exiting");
                return;
            }

            // Load initial page with dynamic render options
            var options = GetCurrentRenderOptions();
            await NavigateToAsync(url, options, cancellationToken);

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
            // Exit alternate screen buffer and restore cursor
            Console.Write("\x1b[?1049l");
            Console.CursorVisible = true;
        }
    }

    /// <summary>
    /// Creates render options from current terminal dimensions.
    /// </summary>
    private static RenderOptions GetCurrentRenderOptions()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;
        return new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = height,
            MaxContentWidth = Math.Clamp(width - 2, 40, 120)
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

    private async Task<bool> HandleCommandAsync(NavigationCommand command, RenderOptions options, CancellationToken cancellationToken)
    {
        var page = _navigationService.CurrentPage;
        var tree = page?.LinkTree;

        switch (command.Type)
        {
            case CommandType.Quit:
                return false;

            case CommandType.MoveDown:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionList)
                {
                    var maxColIdx = (_collections?.Count ?? 0) - 1;
                    if (maxColIdx >= 0)
                    {
                        _navigationService.CollectionSelectedIndex =
                            Math.Min(_navigationService.CollectionSelectedIndex + 1, maxColIdx);
                    }
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    var activeCol = _navigationService.ActiveCollection;
                    if (activeCol != null)
                    {
                        var maxItemIdx = activeCol.Items.Count - 1;
                        if (maxItemIdx >= 0)
                        {
                            _navigationService.CollectionItemSelectedIndex =
                                Math.Min(_navigationService.CollectionItemSelectedIndex + 1, maxItemIdx);
                        }
                    }
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
                {
                    tree?.SelectNext();
                    AdjustScrollForSelection(tree, options);
                }
                else
                {
                    // Line-based scrolling for reader view
                    EnsureLineCache(options);
                    var viewportHeight = GetReaderViewportHeight(options);
                    var maxOffset = Math.Max(0, (_cachedLines?.Count ?? 0) - viewportHeight);
                    _navigationService.SetScrollOffset(
                        Math.Min(_navigationService.CurrentContext.ScrollOffset + 1, maxOffset));
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.MoveUp:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionList)
                {
                    _navigationService.CollectionSelectedIndex =
                        Math.Max(_navigationService.CollectionSelectedIndex - 1, 0);
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    _navigationService.CollectionItemSelectedIndex =
                        Math.Max(_navigationService.CollectionItemSelectedIndex - 1, 0);
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
                {
                    tree?.SelectPrevious();
                    AdjustScrollForSelection(tree, options);
                }
                else
                {
                    _navigationService.SetScrollOffset(Math.Max(0, _navigationService.CurrentContext.ScrollOffset - 1));
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.ExpandNode:
                tree?.CurrentSelection?.Expand();
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.CollapseNode:
                tree?.CurrentSelection?.Collapse();
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.ToggleNode:
                tree?.ToggleCollapse();
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.ActivateLink:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionList)
                {
                    // Open the selected collection
                    if (_collections != null && _navigationService.CollectionSelectedIndex < _collections.Count)
                    {
                        var selectedCollection = _collections[_navigationService.CollectionSelectedIndex];
                        _navigationService.EnterCollection(selectedCollection);
                        await RenderCurrentPageAsync(options, cancellationToken);
                    }
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    // Navigate to the selected item's URL
                    var activeCol = _navigationService.ActiveCollection;
                    if (activeCol != null && _navigationService.CollectionItemSelectedIndex < activeCol.Items.Count)
                    {
                        var selectedItem = activeCol.Items[_navigationService.CollectionItemSelectedIndex];
                        _navigationService.SaveCollectionReturnPoint();
                        await NavigateToAsync(selectedItem.Url, options, cancellationToken);

                        // Mark item as read
                        try
                        {
                            using var markScope = _scopeFactory.CreateScope();
                            var markService = CreateCollectionService(markScope);
                            await markService.MarkItemAsReadAsync(activeCol.Id, selectedItem.Id, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to mark item as read");
                        }

                        // Default to reader view if the page has readable content
                        if (_navigationService.CurrentPage?.HasReadableContent() == true)
                        {
                            _navigationService.SetViewMode(ViewMode.Readable);
                            InvalidateLineCache();
                            await RenderCurrentPageAsync(options, cancellationToken);
                        }
                    }
                }
                else
                {
                    var selectedNode = tree?.GetSelectedNode();
                    if (selectedNode != null)
                    {
                        // Group headers toggle collapse instead of navigating
                        if (selectedNode.IsGroupHeader)
                        {
                            selectedNode.ToggleCollapse();
                            await RenderCurrentPageAsync(options, cancellationToken);
                        }
                        else if (!string.IsNullOrEmpty(selectedNode.Link.Url))
                        {
                            await NavigateToAsync(selectedNode.Link.Url, options, cancellationToken);

                            // Default to reader view if the page has readable content
                            if (_navigationService.CurrentPage?.HasReadableContent() == true)
                            {
                                _navigationService.SetViewMode(ViewMode.Readable);
                                InvalidateLineCache();
                                await RenderCurrentPageAsync(options, cancellationToken);
                            }
                        }
                    }
                }

                break;

            case CommandType.GoBack:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    _navigationService.ExitToCollectionList();
                    await RenderCurrentPageAsync(options, cancellationToken);
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionList)
                {
                    _navigationService.ExitCollections();
                    InvalidateLineCache();
                    await RenderCurrentPageAsync(options, cancellationToken);
                }
                else if (_navigationService.TryRestoreCollectionReturnPoint())
                {
                    // Returned to collection items from an article
                    await RefreshCollectionsAsync(cancellationToken);
                    await RenderCurrentPageAsync(options, cancellationToken);
                }
                else
                {
                    var previousPage = _navigationService.GoBack();
                    if (previousPage != null)
                    {
                        InvalidateLineCache();
                        await RenderCurrentPageAsync(options, cancellationToken);
                    }
                }

                break;

            case CommandType.GoForward:
                var nextPage = _navigationService.GoForward();
                if (nextPage != null)
                {
                    InvalidateLineCache();
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                break;

            case CommandType.SwitchView:
                _navigationService.ToggleViewMode();
                InvalidateLineCache();
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.SwitchToHierarchical:
                _navigationService.SetViewMode(ViewMode.Hierarchical);
                InvalidateLineCache();
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.SwitchToReadable:
                _navigationService.SetViewMode(ViewMode.Readable);
                InvalidateLineCache();
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.PageDown:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Readable)
                {
                    // Half-page scrolling for reader view
                    EnsureLineCache(options);
                    var vpHeight = GetReaderViewportHeight(options);
                    var halfPage = Math.Max(1, vpHeight / 2);
                    var maxOff = Math.Max(0, (_cachedLines?.Count ?? 0) - vpHeight);
                    _navigationService.SetScrollOffset(
                        Math.Min(_navigationService.CurrentContext.ScrollOffset + halfPage, maxOff));
                }
                else
                {
                    _navigationService.SetScrollOffset(_navigationService.CurrentContext.ScrollOffset + 10);
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.PageUp:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Readable)
                {
                    // Half-page scrolling for reader view
                    EnsureLineCache(options);
                    var vpHeightUp = GetReaderViewportHeight(options);
                    var halfPageUp = Math.Max(1, vpHeightUp / 2);
                    _navigationService.SetScrollOffset(
                        Math.Max(0, _navigationService.CurrentContext.ScrollOffset - halfPageUp));
                }
                else
                {
                    _navigationService.SetScrollOffset(Math.Max(0, _navigationService.CurrentContext.ScrollOffset - 10));
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.GoToTop:
                _navigationService.SetScrollOffset(0);
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical && tree != null)
                {
                    // Select first node
                    var firstNode = tree.GetAllNodes().FirstOrDefault();
                    if (firstNode != null)
                    {
                        tree.SelectNodeById(firstNode.Id);
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.GoToBottom:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Readable && page?.ReadableContent != null)
                {
                    EnsureLineCache(options);
                    var vpHeightBottom = GetReaderViewportHeight(options);
                    _navigationService.SetScrollOffset(
                        Math.Max(0, (_cachedLines?.Count ?? 0) - vpHeightBottom));
                }
                else if (tree != null)
                {
                    // Select last visible node
                    var lastNode = tree.GetVisibleNodes().LastOrDefault();
                    if (lastNode != null)
                    {
                        tree.SelectNodeById(lastNode.Id);
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.Refresh:
                if (page != null)
                {
                    await NavigateToAsync(page.Url, options, cancellationToken);
                }

                break;

            case CommandType.ShowHelp:
                Console.Clear();
                Console.WriteLine(_inputHandler.GetHelpText());
                Console.ReadKey(intercept: true);
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.Navigate:
                if (!string.IsNullOrEmpty(command.TargetUrl))
                {
                    await NavigateToAsync(command.TargetUrl, options, cancellationToken);
                }

                break;

            case CommandType.OpenCommandLine:
                var input = await _inputHandler.PromptForInputAsync(":", cancellationToken);
                if (!string.IsNullOrWhiteSpace(input))
                {
                    await HandleCommandLineInput(input.Trim(), options, cancellationToken);
                }
                else
                {
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                break;

            case CommandType.Search:
                var query = await _inputHandler.PromptForInputAsync("/", cancellationToken);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    _navigationService.SetSearchQuery(query);
                    ScrollToSearchMatch(0, options);
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.SearchNext:
                if (!string.IsNullOrEmpty(_navigationService.CurrentContext.SearchQuery))
                {
                    var nextIndex = _navigationService.CurrentContext.SearchMatchIndex + 1;
                    ScrollToSearchMatch(nextIndex, options);
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                break;

            case CommandType.SearchPrevious:
                if (!string.IsNullOrEmpty(_navigationService.CurrentContext.SearchQuery))
                {
                    var prevIndex = Math.Max(0, _navigationService.CurrentContext.SearchMatchIndex - 1);
                    ScrollToSearchMatch(prevIndex, options);
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                break;

            case CommandType.SaveToCollection:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
                {
                    var saveNode = tree?.GetSelectedNode();
                    if (saveNode != null && !saveNode.IsGroupHeader && !string.IsNullOrEmpty(saveNode.Link.Url))
                    {
                        try
                        {
                            using var saveScope = _scopeFactory.CreateScope();
                            var saveService = CreateCollectionService(saveScope);
                            await saveService.SaveToDefaultCollectionAsync(
                                saveNode.Link.Url, saveNode.Link.DisplayText, cancellationToken);
                            _logger.LogInformation("Saved to default collection: {Title}", saveNode.Link.DisplayText);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to save to default collection");
                        }
                    }
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionList)
                {
                    // Set as default collection
                    if (_collections != null && _navigationService.CollectionSelectedIndex < _collections.Count)
                    {
                        var col = _collections[_navigationService.CollectionSelectedIndex];
                        try
                        {
                            using var defaultScope = _scopeFactory.CreateScope();
                            var defaultService = CreateCollectionService(defaultScope);
                            await defaultService.SetDefaultCollectionAsync(col.Id, cancellationToken);
                            _defaultCollectionId = col.Id;
                            _logger.LogInformation("Set default collection: {Name}", col.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to set default collection");
                        }
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.SaveToSpecific:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
                {
                    var saveSpecNode = tree?.GetSelectedNode();
                    if (saveSpecNode != null && !saveSpecNode.IsGroupHeader && !string.IsNullOrEmpty(saveSpecNode.Link.Url))
                    {
                        var collectionName = await _inputHandler.PromptForInputAsync("Save to collection: ", cancellationToken);
                        if (!string.IsNullOrWhiteSpace(collectionName))
                        {
                            try
                            {
                                using var specScope = _scopeFactory.CreateScope();
                                var specService = CreateCollectionService(specScope);
                                await specService.SaveToCollectionByNameAsync(
                                    collectionName, saveSpecNode.Link.Url, saveSpecNode.Link.DisplayText, cancellationToken);
                                _logger.LogInformation("Saved to collection '{Collection}': {Title}",
                                    collectionName, saveSpecNode.Link.DisplayText);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to save to collection");
                            }
                        }
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.OpenCollections:
                await EnterCollectionsModeAsync(options, cancellationToken);
                break;

            case CommandType.DeleteItem:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    var delCol = _navigationService.ActiveCollection;
                    if (delCol != null && _navigationService.CollectionItemSelectedIndex < delCol.Items.Count)
                    {
                        var delItem = delCol.Items[_navigationService.CollectionItemSelectedIndex];
                        try
                        {
                            using var delItemScope = _scopeFactory.CreateScope();
                            var delItemService = CreateCollectionService(delItemScope);
                            await delItemService.RemoveItemAsync(delCol.Id, delItem.Id, cancellationToken);
                            await RefreshCollectionsAsync(cancellationToken);
                            // Adjust selected index if we deleted the last item
                            // Use refreshed active collection, not the stale delCol reference
                            var refreshedCol = _navigationService.ActiveCollection;
                            var refreshedItemCount = refreshedCol?.Items.Count ?? 0;
                            if (_navigationService.CollectionItemSelectedIndex >= refreshedItemCount)
                            {
                                _navigationService.CollectionItemSelectedIndex = Math.Max(0, refreshedItemCount - 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove item");
                        }
                    }
                }
                else if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionList)
                {
                    if (_collections != null && _navigationService.CollectionSelectedIndex < _collections.Count)
                    {
                        var delCollection = _collections[_navigationService.CollectionSelectedIndex];
                        try
                        {
                            using var delColScope = _scopeFactory.CreateScope();
                            var delColService = CreateCollectionService(delColScope);
                            await delColService.DeleteCollectionAsync(delCollection.Id, cancellationToken);
                            await RefreshCollectionsAsync(cancellationToken);
                            if (_navigationService.CollectionSelectedIndex >= _collections.Count)
                            {
                                _navigationService.CollectionSelectedIndex = Math.Max(0, _collections.Count - 1);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete collection");
                        }
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.ReorderUp:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    var reorderUpCol = _navigationService.ActiveCollection;
                    if (reorderUpCol != null && _navigationService.CollectionItemSelectedIndex < reorderUpCol.Items.Count)
                    {
                        var moveItem = reorderUpCol.Items[_navigationService.CollectionItemSelectedIndex];
                        try
                        {
                            using var moveUpScope = _scopeFactory.CreateScope();
                            var moveUpService = CreateCollectionService(moveUpScope);
                            await moveUpService.MoveItemUpAsync(reorderUpCol.Id, moveItem.Id, cancellationToken);
                            await RefreshCollectionsAsync(cancellationToken);
                            _navigationService.CollectionItemSelectedIndex =
                                Math.Max(0, _navigationService.CollectionItemSelectedIndex - 1);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to move item up");
                        }
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.ReorderDown:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    var reorderDownCol = _navigationService.ActiveCollection;
                    if (reorderDownCol != null && _navigationService.CollectionItemSelectedIndex < reorderDownCol.Items.Count)
                    {
                        var moveDownItem = reorderDownCol.Items[_navigationService.CollectionItemSelectedIndex];
                        try
                        {
                            using var moveDownScope = _scopeFactory.CreateScope();
                            var moveDownService = CreateCollectionService(moveDownScope);
                            await moveDownService.MoveItemDownAsync(reorderDownCol.Id, moveDownItem.Id, cancellationToken);
                            await RefreshCollectionsAsync(cancellationToken);
                            // Use refreshed collection count, not the stale reorderDownCol reference
                            var refreshedReorderCol = _navigationService.ActiveCollection;
                            var refreshedCount = refreshedReorderCol?.Items.Count ?? 0;
                            _navigationService.CollectionItemSelectedIndex =
                                Math.Min(Math.Max(0, refreshedCount - 1), _navigationService.CollectionItemSelectedIndex + 1);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to move item down");
                        }
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;
        }

        return true;
    }

    private async Task RenderCurrentPageAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        var viewMode = _navigationService.CurrentContext.ViewMode;

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
    /// Handles command line input (text entered after ':').
    /// Supports: open/go URL, quit, back, forward, help.
    /// </summary>
    private async Task HandleCommandLineInput(string input, RenderOptions options, CancellationToken cancellationToken)
    {
        // Parse command - support "open URL", "go URL", or just a bare URL
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "q" or "quit":
                // Signal quit by navigating away - caller will handle
                return;

            case "back" or "b":
                var prevPage = _navigationService.GoBack();
                if (prevPage != null)
                {
                    InvalidateLineCache();
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                return;

            case "forward" or "f":
                var fwdPage = _navigationService.GoForward();
                if (fwdPage != null)
                {
                    InvalidateLineCache();
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                return;

            case "help" or "h":
                Console.Clear();
                Console.WriteLine(_inputHandler.GetHelpText());
                Console.ReadKey(intercept: true);
                await RenderCurrentPageAsync(options, cancellationToken);
                return;

            case "open" or "go" or "o":
                if (parts.Length > 1)
                {
                    var url = NormalizeUrl(parts[1]);
                    await NavigateToAsync(url, options, cancellationToken);
                }
                else
                {
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                return;

            case "collections" or "readlater":
                await EnterCollectionsModeAsync(options, cancellationToken);
                return;

            case "new":
                if (parts.Length > 1)
                {
                    try
                    {
                        using var newScope = _scopeFactory.CreateScope();
                        var newService = CreateCollectionService(newScope);
                        await newService.CreateCollectionAsync(parts[1], cancellationToken);
                        await RefreshCollectionsAsync(cancellationToken);
                        _logger.LogInformation("Created collection: {Name}", parts[1]);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to create collection");
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                return;

            case "rename":
                if (parts.Length > 1 && _navigationService.CurrentContext.ViewMode == ViewMode.CollectionList)
                {
                    if (_collections != null && _navigationService.CollectionSelectedIndex < _collections.Count)
                    {
                        var renameCol = _collections[_navigationService.CollectionSelectedIndex];
                        try
                        {
                            using var renameScope = _scopeFactory.CreateScope();
                            var renameService = CreateCollectionService(renameScope);
                            await renameService.RenameCollectionAsync(renameCol.Id, parts[1], cancellationToken);
                            await RefreshCollectionsAsync(cancellationToken);
                            _logger.LogInformation("Renamed collection to: {Name}", parts[1]);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to rename collection");
                        }
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                return;

            case "clear":
                if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    var clearCol = _navigationService.ActiveCollection;
                    if (clearCol != null)
                    {
                        try
                        {
                            using var clearScope = _scopeFactory.CreateScope();
                            var clearService = CreateCollectionService(clearScope);
                            await clearService.ClearCollectionAsync(clearCol.Id, cancellationToken);
                            await RefreshCollectionsAsync(cancellationToken);
                            _navigationService.CollectionItemSelectedIndex = 0;
                            _logger.LogInformation("Cleared collection: {Name}", clearCol.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to clear collection");
                        }
                    }
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                return;

            case "export":
                await HandleExportCommandAsync(parts.Length > 1 ? parts[1] : null, options, cancellationToken);
                return;

            default:
                // Treat the entire input as a URL if it looks like one
                var navigateUrl = NormalizeUrl(input);
                await NavigateToAsync(navigateUrl, options, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// Normalizes user input into a URL by adding https:// if needed.
    /// </summary>
    private static string NormalizeUrl(string input)
    {
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + input;
        }

        return input;
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
        var contentHeight = options.ContentHeight;

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
    /// Pre-wraps all paragraphs into a flat list of display lines for the reader view.
    /// </summary>
    private static List<string> WrapAllContent(ReadableContent content, int maxWidth)
    {
        var allLines = new List<string>();
        foreach (var paragraph in content.Paragraphs)
        {
            var wrapped = WrapText(paragraph, maxWidth - 4);
            foreach (var line in wrapped)
            {
                allLines.Add($"  {line}");
            }

            allLines.Add(string.Empty);
        }

        return allLines;
    }

    /// <summary>
    /// Wraps text into lines that fit within maxWidth.
    /// </summary>
    private static List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = string.Empty;

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxWidth)
            {
                if (!string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(currentLine);
                }

                currentLine = word;
            }
            else
            {
                currentLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
        {
            lines.Add(currentLine);
        }

        return lines;
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
    /// Creates a scoped ICollectionService instance for database operations.
    /// ICollectionService is registered as Scoped (depends on DbContext) while the orchestrator is Singleton.
    /// </summary>
    private ICollectionService CreateCollectionService(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<ICollectionService>();
    }

    /// <summary>
    /// Enters collections mode by loading collections and switching the view.
    /// </summary>
    private async Task EnterCollectionsModeAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        try
        {
            _navigationService.EnterCollections();
            await RefreshCollectionsAsync(cancellationToken);
            await RenderCurrentPageAsync(options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enter collections mode");
            _navigationService.ExitCollections();
            await RenderCurrentPageAsync(options, cancellationToken);
        }
    }

    /// <summary>
    /// Handles the :export command. Exports the active collection using the specified format.
    /// Usage: :export [format] (default: urls). Available formats depend on registered exporters.
    /// </summary>
    private async Task HandleExportCommandAsync(string? format, RenderOptions options, CancellationToken cancellationToken)
    {
        var collection = _navigationService.ActiveCollection;
        if (collection == null)
        {
            _logger.LogWarning("No active collection to export. Open a collection first.");
            await RenderCurrentPageAsync(options, cancellationToken);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var exporters = scope.ServiceProvider.GetServices<ICollectionExporter>();

            var requestedFormat = format?.ToLowerInvariant() ?? "urls";
            var exporter = exporters.FirstOrDefault(e =>
                string.Equals(e.Format, requestedFormat, StringComparison.OrdinalIgnoreCase));

            if (exporter == null)
            {
                var available = string.Join(", ", exporters.Select(e => e.Format));
                _logger.LogWarning("Unknown export format '{Format}'. Available: {Available}", requestedFormat, available);
                await RenderCurrentPageAsync(options, cancellationToken);
                return;
            }

            var outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "termreader-exports");
            Directory.CreateDirectory(outputDir);

            var safeName = string.Join("_", collection.Name.Split(Path.GetInvalidFileNameChars()));
            var outputPath = Path.Combine(outputDir, $"{safeName}.{requestedFormat}");

            await exporter.ExportAsync(collection, new ExportOptions(outputPath), cancellationToken);
            _logger.LogInformation("Exported collection '{Name}' to {Path}", collection.Name, outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to export collection");
        }

        await RenderCurrentPageAsync(options, cancellationToken);
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
                    _navigationService.CollectionItemSelectedIndex = savedItemIndex;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh collections");
        }
    }
}
