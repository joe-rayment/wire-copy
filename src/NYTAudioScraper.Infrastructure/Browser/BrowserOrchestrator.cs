// Educational and personal use only.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.DTOs.Browser;
using NYTAudioScraper.Application.Interfaces.Browser;
using NYTAudioScraper.Domain.Entities.Browser;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;

namespace NYTAudioScraper.Infrastructure.Browser;

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
    private readonly Configuration.BrowserConfiguration _browserConfig;
    private readonly ILogger<BrowserOrchestrator> _logger;

    public BrowserOrchestrator(
        IPageLoader pageLoader,
        ILinkExtractor linkExtractor,
        INavigationTreeBuilder treeBuilder,
        IReadableContentExtractor contentExtractor,
        IPageRenderer renderer,
        IInputHandler inputHandler,
        NavigationService navigationService,
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
                _renderer.RenderReadable(page, context, options);
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Main browser loop. Handles user input and navigation.
    /// </summary>
    public async Task RunAsync(string? initialUrl = null, CancellationToken cancellationToken = default)
    {
        var options = new RenderOptions
        {
            TerminalWidth = Console.WindowWidth,
            TerminalHeight = Console.WindowHeight
        };

        try
        {
            // Get initial URL if not provided
            var url = initialUrl ?? await _inputHandler.PromptForUrlAsync(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogInformation("No URL provided, exiting");
                return;
            }

            // Load initial page
            await NavigateToAsync(url, options, cancellationToken);

            // Main input loop
            while (!cancellationToken.IsCancellationRequested)
            {
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
    }

    private async Task NavigateToAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var page = await LoadPageAsync(url, cancellationToken);
            _navigationService.NavigateTo(page);
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
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
                {
                    tree?.SelectNext();
                    AdjustScrollForSelection(tree, options);
                }
                else
                {
                    _navigationService.SetScrollOffset(_navigationService.CurrentContext.ScrollOffset + 1);
                }

                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.MoveUp:
                if (_navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
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
                            await RenderCurrentPageAsync(options, cancellationToken);
                        }
                    }
                }

                break;

            case CommandType.GoBack:
                var previousPage = _navigationService.GoBack();
                if (previousPage != null)
                {
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                break;

            case CommandType.GoForward:
                var nextPage = _navigationService.GoForward();
                if (nextPage != null)
                {
                    await RenderCurrentPageAsync(options, cancellationToken);
                }

                break;

            case CommandType.SwitchView:
                _navigationService.ToggleViewMode();
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.SwitchToHierarchical:
                _navigationService.SetViewMode(ViewMode.Hierarchical);
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.SwitchToReadable:
                _navigationService.SetViewMode(ViewMode.Readable);
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.PageDown:
                _navigationService.SetScrollOffset(_navigationService.CurrentContext.ScrollOffset + 10);
                await RenderCurrentPageAsync(options, cancellationToken);
                break;

            case CommandType.PageUp:
                _navigationService.SetScrollOffset(Math.Max(0, _navigationService.CurrentContext.ScrollOffset - 10));
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
                    _navigationService.SetScrollOffset(Math.Max(0, page.ReadableContent.Paragraphs.Count - 5));
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
        }

        return true;
    }

    private async Task RenderCurrentPageAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        var page = _navigationService.CurrentPage;
        if (page == null)
        {
            return;
        }

        await RenderAsync(page, _navigationService.CurrentContext.ViewMode, options, cancellationToken);
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
}
