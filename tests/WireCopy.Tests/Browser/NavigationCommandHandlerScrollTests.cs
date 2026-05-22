// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for NavigationCommandHandler scroll behavior in Readable view mode.
/// Verifies MoveDown/MoveUp/PageDown/PageUp/GoToBottom advance scroll correctly
/// when CachedLines is populated by EnsureLineCache.
/// </summary>
[Trait("Category", "Unit")]
public class NavigationCommandHandlerScrollTests
{
    private readonly NavigationService _navigationService;
    private readonly LineCacheManager _lineCacheManager;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly List<string> _testLines;
    private bool _renderCalled;

    private const int ViewportHeight = 20;

    public NavigationCommandHandlerScrollTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        page.SetReadableContent(Domain.Entities.Browser.ReadableContent.Create(
            "Test", "Test content", new List<string> { "Paragraph 1" }));
        _navigationService.NavigateTo(page);
        _navigationService.SetViewMode(ViewMode.Readable);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 76
        };

        // 50 lines of content — more than viewport height
        _testLines = Enumerable.Range(1, 50).Select(i => $"  Line {i}").ToList();

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _lineCacheManager = new LineCacheManager(_navigationService, themeProvider);
        _lineCacheManager.SetCacheForTesting(_testLines, 76);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = _lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
            RenderCurrentPageAsync = (_, _) =>
            {
                _renderCalled = true;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => ViewportHeight,
            GetHierarchicalViewportHeight = _ => ViewportHeight,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    #region HandleMoveDown - Readable scroll

    [Fact]
    public async Task HandleMoveDown_Readable_IncrementsCursorByOne()
    {
        _navigationService.SetReaderCursorLine(0);

        await NavigationCommandHandler.HandleMoveDown(_ctx, new NavigationCommand { Type = CommandType.MoveDown }, _options, CancellationToken.None);

        _navigationService.ReaderCursorLine.Should().Be(1);
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMoveDown_Readable_IncrementsCursorFromExistingPosition()
    {
        _navigationService.SetReaderCursorLine(10);

        await NavigationCommandHandler.HandleMoveDown(_ctx, new NavigationCommand { Type = CommandType.MoveDown }, _options, CancellationToken.None);

        _navigationService.ReaderCursorLine.Should().Be(11);
    }

    [Fact]
    public async Task HandleMoveDown_Readable_ClampsCursorToLastLine()
    {
        // 50 lines, last index = 49
        _navigationService.SetReaderCursorLine(49);

        await NavigationCommandHandler.HandleMoveDown(_ctx, new NavigationCommand { Type = CommandType.MoveDown }, _options, CancellationToken.None);

        _navigationService.ReaderCursorLine.Should().Be(49);
    }

    [Fact]
    public async Task HandleMoveDown_Readable_PopulatesCachedLinesViaEnsureLineCache()
    {
        _lineCacheManager.InvalidateLineCache();

        await NavigationCommandHandler.HandleMoveDown(_ctx, new NavigationCommand { Type = CommandType.MoveDown }, _options, CancellationToken.None);

        // EnsureLineCache should rebuild from the page's ReadableContent
        _lineCacheManager.CachedLines.Should().NotBeNull();
    }

    #endregion

    #region HandleMoveUp - Readable scroll

    [Fact]
    public async Task HandleMoveUp_Readable_DecrementsCursorByOne()
    {
        _navigationService.SetReaderCursorLine(10);

        await NavigationCommandHandler.HandleMoveUp(_ctx, new NavigationCommand { Type = CommandType.MoveUp }, _options, CancellationToken.None);

        _navigationService.ReaderCursorLine.Should().Be(9);
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMoveUp_Readable_ClampsToZero()
    {
        _navigationService.SetScrollOffset(0);

        await NavigationCommandHandler.HandleMoveUp(_ctx, new NavigationCommand { Type = CommandType.MoveUp }, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task HandleMoveUp_Readable_DecrementsFromOne()
    {
        _navigationService.SetScrollOffset(1);

        await NavigationCommandHandler.HandleMoveUp(_ctx, new NavigationCommand { Type = CommandType.MoveUp }, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    #endregion

    #region HandlePageDown - Readable scroll

    [Fact]
    public async Task HandlePageDown_Readable_MovesCursorByHalfPage()
    {
        _navigationService.SetReaderCursorLine(0);

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        // halfPage = max(1, 20/2) = 10
        _navigationService.ReaderCursorLine.Should().Be(10);
    }

    [Fact]
    public async Task HandlePageDown_Readable_ClampsCursorToLastLine()
    {
        // 50 lines, cursor at 45, halfPage = 10 → would be 55, clamped to 49
        _navigationService.SetReaderCursorLine(45);

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        _navigationService.ReaderCursorLine.Should().Be(49);
    }

    #endregion

    #region HandlePageUp - Readable scroll

    [Fact]
    public async Task HandlePageUp_Readable_MovesCursorBackByHalfPage()
    {
        _navigationService.SetReaderCursorLine(20);

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        // halfPage = 10, 20 - 10 = 10
        _navigationService.ReaderCursorLine.Should().Be(10);
    }

    [Fact]
    public async Task HandlePageUp_Readable_ClampsToZero()
    {
        _navigationService.SetScrollOffset(5);

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        // halfPage = 10, 5 - 10 = -5, clamped to 0
        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    #endregion

    #region HandleGoToBottom - Readable scroll

    [Fact]
    public async Task HandleGoToBottom_Readable_ScrollsToEnd()
    {
        _navigationService.SetScrollOffset(0);

        await NavigationCommandHandler.HandleGoToBottom(_ctx, new NavigationCommand { Type = CommandType.GoToBottom }, _options, CancellationToken.None);

        // maxOffset = max(0, 50 - 20) = 30
        _navigationService.CurrentContext.ScrollOffset.Should().Be(30);
    }

    [Fact]
    public async Task HandleGoToBottom_Readable_AlreadyAtEnd()
    {
        _navigationService.SetScrollOffset(30);

        await NavigationCommandHandler.HandleGoToBottom(_ctx, new NavigationCommand { Type = CommandType.GoToBottom }, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(30);
    }

    #endregion

    #region HandleGoToTop - Readable scroll

    [Fact]
    public async Task HandleGoToTop_Readable_ScrollsToStart()
    {
        _navigationService.SetScrollOffset(25);

        await NavigationCommandHandler.HandleGoToTop(_ctx, new NavigationCommand { Type = CommandType.GoToTop }, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    #endregion

    #region HandleGoToTop - CollectionItems

    [Fact]
    public async Task HandleGoToTop_CollectionItems_SetsIndexToFirstItem()
    {
        var collection = Domain.Entities.Collections.Collection.Create("Test");
        collection.AddItem("https://example.com/1", "Item 1");
        collection.AddItem("https://example.com/2", "Item 2");
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);

        // Initially at CTA button (-1), move to item 1
        _navigationService.CollectionItemSelectedIndex = 1;

        await NavigationCommandHandler.HandleGoToTop(_ctx, new NavigationCommand { Type = CommandType.GoToTop }, _options, CancellationToken.None);

        // Should go to first item (0), NOT CTA button (-1)
        _navigationService.CollectionItemSelectedIndex.Should().Be(0,
            "gg should go to first list item, not CTA button");
    }

    #endregion

    #region HandleGoToTop - Hierarchical with collapsed groups

    [Fact]
    public async Task HandleGoToTop_Hierarchical_SelectsFirstVisibleNode()
    {
        // Arrange — build a tree where Navigation group is first (collapsed by default)
        // and Content links come after. GetAllNodes returns collapsed children,
        // but GetVisibleNodes skips them.
        var groupedLinks = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Navigation] = new()
            {
                new LinkInfo { Url = "https://example.com/nav1", DisplayText = "Home", Type = LinkType.Navigation, ImportanceScore = 30 },
                new LinkInfo { Url = "https://example.com/nav2", DisplayText = "About", Type = LinkType.Navigation, ImportanceScore = 30 },
            },
            [LinkType.Content] = new()
            {
                new LinkInfo { Url = "https://example.com/article1", DisplayText = "First Article Headline", Type = LinkType.Content, ImportanceScore = 70 },
            },
        };

        var tree = NavigationTree.BuildWithGroups(groupedLinks);

        // Content is first in group order, so first visible node should be the content link
        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        page.SetLinkTree(tree);

        _navigationService.NavigateTo(page);
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        // Select the last node to simulate being elsewhere in the tree
        var visibleNodes = tree.GetVisibleNodes().ToList();
        var lastVisible = visibleNodes.LastOrDefault();
        if (lastVisible != null)
        {
            tree.SelectNodeById(lastVisible.Id);
        }

        // Act
        await NavigationCommandHandler.HandleGoToTop(_ctx, new NavigationCommand { Type = CommandType.GoToTop }, _options, CancellationToken.None);

        // Assert — selection should be the first VISIBLE node, not a collapsed child
        var firstVisible = tree.GetVisibleNodes().First();
        tree.CurrentSelection.Should().Be(firstVisible,
            "GoToTop should select first visible node, not a collapsed child");
        tree.CurrentSelection!.IsSelected.Should().BeTrue();
    }

    #endregion
}
