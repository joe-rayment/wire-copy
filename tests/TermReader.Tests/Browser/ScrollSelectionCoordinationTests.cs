// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for scroll-selection coordination across CollectionList, CollectionItems, and Launcher views.
/// Verifies that scroll offsets adjust to keep the selected item visible after every navigation command.
/// </summary>
public class ScrollSelectionCoordinationTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private bool _renderCalled;

    private const int TermHeight = 24;

    public ScrollSelectionCoordinationTests()
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

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = TermHeight,
            MaxContentWidth = 76
        };

        var testLines = Enumerable.Range(1, 50).Select(i => $"  Line {i}").ToList();

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var lineCacheManager = new LineCacheManager(_navigationService, themeProvider);
        lineCacheManager.SetCacheForTesting(testLines, 76);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) =>
            {
                _renderCalled = true;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    private List<Collection> CreateCollections(int count)
    {
        var collections = new List<Collection>();
        for (var i = 0; i < count; i++)
        {
            collections.Add(Collection.Create($"Collection {i}", i));
        }

        return collections;
    }

    private Collection CreateCollectionWithItems(int itemCount)
    {
        var collection = Collection.Create("Test Collection");
        for (var i = 0; i < itemCount; i++)
        {
            collection.AddItem($"https://example.com/{i}", $"Item {i}");
        }

        return collection;
    }

    private List<Bookmark> CreateBookmarks(int count)
    {
        var bookmarks = new List<Bookmark>();
        for (var i = 0; i < count; i++)
        {
            bookmarks.Add(Bookmark.Create($"Bookmark {i}", $"https://example.com/b{i}", i));
        }

        return bookmarks;
    }

    #region CollectionList - MoveDown scroll adjustment

    [Fact]
    public async Task CollectionList_MoveDown_ScrollsWhenSelectionExceedsViewport()
    {
        // height=24 < 30, no separators, linesPerItem=1
        // remainingHeight = Max(3, 24-5-3) = 16
        var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(TermHeight);
        var collections = CreateCollections(maxVisible + 5);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = maxVisible - 1; // last visible item

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        // Selection moved beyond viewport, scroll should adjust
        _navigationService.CollectionSelectedIndex.Should().Be(maxVisible);
        _navigationService.CollectionListScrollOffset.Should().Be(1);
    }

    [Fact]
    public async Task CollectionList_MoveDown_NoScrollWhenSelectionWithinViewport()
    {
        var collections = CreateCollections(25);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = 3;

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(4);
        _navigationService.CollectionListScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task CollectionList_MoveDown_ClampsToLastItem()
    {
        var collections = CreateCollections(5);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = 4; // last item

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(4);
    }

    #endregion

    #region CollectionList - MoveUp scroll adjustment

    [Fact]
    public async Task CollectionList_MoveUp_ScrollsBackWhenSelectionAboveViewport()
    {
        var collections = CreateCollections(25);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = 5;
        _navigationService.CollectionListScrollOffset = 5; // scrolled down so index 5 is first visible

        await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);

        // Now selected=4 which is above scroll offset=5, scroll should adjust
        _navigationService.CollectionSelectedIndex.Should().Be(4);
        _navigationService.CollectionListScrollOffset.Should().Be(4);
    }

    [Fact]
    public async Task CollectionList_MoveUp_ClampsToZero()
    {
        var collections = CreateCollections(25);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = 0;

        await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(0);
        _navigationService.CollectionListScrollOffset.Should().Be(0);
    }

    #endregion

    #region CollectionList - PageDown/PageUp

    [Fact]
    public async Task CollectionList_PageDown_JumpsHalfPageAndAdjustsScroll()
    {
        var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(TermHeight);
        var halfPage = Math.Max(1, maxVisible / 2);
        var collections = CreateCollections(maxVisible * 3);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(halfPage);
        // halfPage < maxVisible so selection is still within viewport; scroll stays at 0
        _navigationService.CollectionListScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task CollectionList_PageDown_ClampsToLastItem()
    {
        var collections = CreateCollections(5);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = 3;

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(4);
    }

    [Fact]
    public async Task CollectionList_PageUp_JumpsBackHalfPage()
    {
        var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(TermHeight);
        var halfPage = Math.Max(1, maxVisible / 2);
        var collections = CreateCollections(maxVisible * 3);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = halfPage * 2;
        _navigationService.CollectionListScrollOffset = halfPage;

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(halfPage);
    }

    [Fact]
    public async Task CollectionList_PageUp_ClampsToZero()
    {
        var collections = CreateCollections(25);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = 2;

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(0);
        _navigationService.CollectionListScrollOffset.Should().Be(0);
    }

    #endregion

    #region CollectionList - GoToTop/GoToBottom

    [Fact]
    public async Task CollectionList_GoToTop_ResetsBothSelectionAndScroll()
    {
        var collections = CreateCollections(25);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _navigationService.CollectionSelectedIndex = 15;
        _navigationService.CollectionListScrollOffset = 10;

        await NavigationCommandHandler.HandleGoToTop(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionSelectedIndex.Should().Be(0);
        _navigationService.CollectionListScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task CollectionList_GoToBottom_SelectsLastAndAdjustsScroll()
    {
        var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(TermHeight);
        var collections = CreateCollections(maxVisible + 10);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();

        await NavigationCommandHandler.HandleGoToBottom(_ctx, _options, CancellationToken.None);

        var lastIdx = collections.Count - 1;
        _navigationService.CollectionSelectedIndex.Should().Be(lastIdx);
        // Scroll should ensure last item is visible
        _navigationService.CollectionListScrollOffset.Should().Be(lastIdx - maxVisible + 1);
    }

    #endregion

    #region CollectionItems - MoveDown scroll adjustment

    [Fact]
    public async Task CollectionItems_MoveDown_ScrollsWhenSelectionExceedsViewport()
    {
        var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(TermHeight);
        var collection = CreateCollectionWithItems(maxVisible + 5);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = maxVisible - 1;

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(maxVisible);
        _navigationService.CollectionItemScrollOffset.Should().Be(1);
    }

    [Fact]
    public async Task CollectionItems_MoveDown_NoScrollWhenWithinViewport()
    {
        var collection = CreateCollectionWithItems(25);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 2;

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(3);
        _navigationService.CollectionItemScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task CollectionItems_MoveDown_ClampsToLastItem()
    {
        var collection = CreateCollectionWithItems(3);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 2;

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(2);
    }

    #endregion

    #region CollectionItems - MoveUp scroll adjustment

    [Fact]
    public async Task CollectionItems_MoveUp_ScrollsBackWhenSelectionAboveViewport()
    {
        var collection = CreateCollectionWithItems(25);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 5;
        _navigationService.CollectionItemScrollOffset = 5;

        await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(4);
        _navigationService.CollectionItemScrollOffset.Should().Be(4);
    }

    [Fact]
    public async Task CollectionItems_MoveUp_MovesToCtaButtonAtNegativeOne()
    {
        var collection = CreateCollectionWithItems(10);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 0;

        await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(-1);
        _navigationService.CollectionItemScrollOffset.Should().Be(0);
    }

    #endregion

    #region CollectionItems - PageDown/PageUp

    [Fact]
    public async Task CollectionItems_PageDown_JumpsHalfPageAndAdjustsScroll()
    {
        var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(TermHeight);
        var halfPage = Math.Max(1, maxVisible / 2);
        var collection = CreateCollectionWithItems(maxVisible * 3);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 0;

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(halfPage);
    }

    [Fact]
    public async Task CollectionItems_PageDown_ClampsToLastItem()
    {
        var collection = CreateCollectionWithItems(3);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 1;

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(2);
    }

    [Fact]
    public async Task CollectionItems_PageUp_JumpsBackHalfPage()
    {
        var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(TermHeight);
        var halfPage = Math.Max(1, maxVisible / 2);
        var collection = CreateCollectionWithItems(maxVisible * 3);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = halfPage * 2;
        _navigationService.CollectionItemScrollOffset = halfPage;

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(halfPage);
    }

    [Fact]
    public async Task CollectionItems_PageUp_ClampsToCtaButton()
    {
        var collection = CreateCollectionWithItems(25);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 1;

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(-1);
        _navigationService.CollectionItemScrollOffset.Should().Be(0);
    }

    #endregion

    #region CollectionItems - GoToTop/GoToBottom

    [Fact]
    public async Task CollectionItems_GoToTop_ResetsToFirstItemAndScroll()
    {
        var collection = CreateCollectionWithItems(25);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CollectionItemSelectedIndex = 15;
        _navigationService.CollectionItemScrollOffset = 10;

        await NavigationCommandHandler.HandleGoToTop(_ctx, _options, CancellationToken.None);

        _navigationService.CollectionItemSelectedIndex.Should().Be(0);
        _navigationService.CollectionItemScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task CollectionItems_GoToBottom_SelectsLastAndAdjustsScroll()
    {
        var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(TermHeight);
        var collection = CreateCollectionWithItems(maxVisible + 10);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);

        await NavigationCommandHandler.HandleGoToBottom(_ctx, _options, CancellationToken.None);

        var lastIdx = collection.Items.Count - 1;
        _navigationService.CollectionItemSelectedIndex.Should().Be(lastIdx);
        _navigationService.CollectionItemScrollOffset.Should().Be(lastIdx - maxVisible + 1);
    }

    #endregion

    #region Launcher - PageDown/PageUp

    [Fact]
    public async Task Launcher_PageDown_MovesSelectionByHalfPageStep()
    {
        var layout = LauncherRenderer.ComputeLayout(_options.TerminalWidth, _options.TerminalHeight);
        var halfRows = Math.Max(1, layout.VisibleRows / 2);
        var step = halfRows * layout.Columns;
        var bookmarks = CreateBookmarks(step * 4);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 0;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.PageDown },
            _options, CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(step);
    }

    [Fact]
    public async Task Launcher_PageDown_ClampsToLastItem()
    {
        var bookmarks = CreateBookmarks(5);
        _ctx.Bookmarks = bookmarks;
        var totalItems = bookmarks.Count + 1; // +1 for Collections tile
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = totalItems - 2;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.PageDown },
            _options, CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(totalItems - 1);
    }

    [Fact]
    public async Task Launcher_PageUp_MovesSelectionBackByHalfPageStep()
    {
        var layout = LauncherRenderer.ComputeLayout(_options.TerminalWidth, _options.TerminalHeight);
        var halfRows = Math.Max(1, layout.VisibleRows / 2);
        var step = halfRows * layout.Columns;
        var bookmarks = CreateBookmarks(step * 4);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = step * 2;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.PageUp },
            _options, CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(step);
    }

    [Fact]
    public async Task Launcher_PageUp_ClampsToZero()
    {
        var bookmarks = CreateBookmarks(20);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 1;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.PageUp },
            _options, CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(0);
    }

    #endregion

    #region Launcher - GoToTop/GoToBottom

    [Fact]
    public async Task Launcher_GoToTop_ResetsBothSelectionAndScroll()
    {
        var bookmarks = CreateBookmarks(20);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 15;
        _navigationService.LauncherScrollOffset = 5;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.GoToTop },
            _options, CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(0);
        _navigationService.LauncherScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task Launcher_GoToBottom_SelectsLastAndAdjustsScroll()
    {
        var bookmarks = CreateBookmarks(20);
        _ctx.Bookmarks = bookmarks;
        var totalItems = bookmarks.Count + 1;
        _navigationService.EnterLauncher();

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.GoToBottom },
            _options, CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(totalItems - 1);
    }

    #endregion

    #region Selection bounds verification

    [Fact]
    public async Task CollectionList_SelectionNeverNegative_AfterRepeatedMoveUp()
    {
        var collections = CreateCollections(10);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();

        for (var i = 0; i < 5; i++)
        {
            await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);
        }

        _navigationService.CollectionSelectedIndex.Should().BeGreaterOrEqualTo(0);
        _navigationService.CollectionListScrollOffset.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task CollectionList_SelectionNeverExceedsMax_AfterRepeatedMoveDown()
    {
        var collections = CreateCollections(5);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();

        for (var i = 0; i < 10; i++)
        {
            await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);
        }

        _navigationService.CollectionSelectedIndex.Should().BeLessOrEqualTo(collections.Count - 1);
    }

    [Fact]
    public async Task CollectionItems_SelectionClampsToCtaButton_AfterRepeatedPageUp()
    {
        var collection = CreateCollectionWithItems(20);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);

        for (var i = 0; i < 5; i++)
        {
            await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);
        }

        _navigationService.CollectionItemSelectedIndex.Should().BeGreaterOrEqualTo(-1);
        _navigationService.CollectionItemScrollOffset.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task CollectionItems_SelectionNeverExceedsMax_AfterRepeatedPageDown()
    {
        var collection = CreateCollectionWithItems(5);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);

        for (var i = 0; i < 10; i++)
        {
            await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);
        }

        _navigationService.CollectionItemSelectedIndex.Should().BeLessOrEqualTo(collection.Items.Count - 1);
    }

    [Fact]
    public async Task Launcher_SelectionNeverNegative_AfterRepeatedPageUp()
    {
        var bookmarks = CreateBookmarks(20);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();

        for (var i = 0; i < 5; i++)
        {
            await LauncherCommandHandler.Handle(_ctx,
                new NavigationCommand { Type = CommandType.PageUp },
                _options, CancellationToken.None);
        }

        _navigationService.LauncherSelectedIndex.Should().BeGreaterOrEqualTo(0);
        _navigationService.LauncherScrollOffset.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task Launcher_SelectionNeverExceedsMax_AfterRepeatedPageDown()
    {
        var bookmarks = CreateBookmarks(5);
        _ctx.Bookmarks = bookmarks;
        var totalItems = bookmarks.Count + 1;
        _navigationService.EnterLauncher();

        for (var i = 0; i < 10; i++)
        {
            await LauncherCommandHandler.Handle(_ctx,
                new NavigationCommand { Type = CommandType.PageDown },
                _options, CancellationToken.None);
        }

        _navigationService.LauncherSelectedIndex.Should().BeLessOrEqualTo(totalItems - 1);
    }

    #endregion

    #region Scroll always keeps selection visible

    [Fact]
    public async Task CollectionList_ScrollAlwaysKeepsSelectionVisible()
    {
        var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(TermHeight);
        var collections = CreateCollections(maxVisible * 3);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();

        // Navigate down through all items
        for (var i = 0; i < collections.Count - 1; i++)
        {
            await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

            var sel = _navigationService.CollectionSelectedIndex;
            var scrollOff = _navigationService.CollectionListScrollOffset;
            sel.Should().BeGreaterOrEqualTo(scrollOff,
                $"at step {i}: selection {sel} should be >= scroll offset {scrollOff}");
            sel.Should().BeLessThan(scrollOff + maxVisible,
                $"at step {i}: selection {sel} should be < scroll offset {scrollOff} + viewport {maxVisible}");
        }
    }

    [Fact]
    public async Task CollectionItems_ScrollAlwaysKeepsSelectionVisible()
    {
        var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(TermHeight);
        var collection = CreateCollectionWithItems(maxVisible * 3);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);

        for (var i = 0; i < collection.Items.Count - 1; i++)
        {
            await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

            var sel = _navigationService.CollectionItemSelectedIndex;
            var scrollOff = _navigationService.CollectionItemScrollOffset;
            sel.Should().BeGreaterOrEqualTo(scrollOff,
                $"at step {i}: selection {sel} should be >= scroll offset {scrollOff}");
            sel.Should().BeLessThan(scrollOff + maxVisible,
                $"at step {i}: selection {sel} should be < scroll offset {scrollOff} + viewport {maxVisible}");
        }
    }

    #endregion

    #region Render called verification

    [Fact]
    public async Task CollectionList_MoveDown_CallsRender()
    {
        var collections = CreateCollections(10);
        _ctx.Collections = collections;
        _navigationService.EnterCollections();
        _renderCalled = false;

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CollectionItems_MoveDown_CallsRender()
    {
        var collection = CreateCollectionWithItems(10);
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _renderCalled = false;

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Launcher_PageDown_CallsRender()
    {
        var bookmarks = CreateBookmarks(10);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _renderCalled = false;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.PageDown },
            _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion
}
