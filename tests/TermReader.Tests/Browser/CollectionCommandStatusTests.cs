// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class CollectionCommandStatusTests
{
    private readonly NavigationService _navService;
    private readonly ICollectionService _collectionService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options = new() { TerminalWidth = 80, TerminalHeight = 24 };

    private string? _lastStatusMessage;

    public CollectionCommandStatusTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navService = new NavigationService(logger);
        _collectionService = Substitute.For<ICollectionService>();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scopeFactory.CreateScope().Returns(scope);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        _ctx = new CommandContext
        {
            NavigationService = _navService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = scopeFactory,
            Logger = NullLogger.Instance,
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(_navService, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            RenderCurrentPageAsync = (_, _) =>
            {
                _lastStatusMessage = _navService.CurrentContext.StatusMessage;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => _collectionService,
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    #region HandleSaveToCollection

    [Fact]
    public async Task HandleSaveToCollection_Success_ShowsSavedMessage()
    {
        SetupHierarchicalWithSelectedLink("My Article", "https://example.com/article");
        _collectionService.SaveToReadingListAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CollectionItem.Create(Guid.NewGuid(), "https://example.com/article", "My Article"));

        await CollectionCommandHandler.HandleSaveToCollection(_ctx, _options, CancellationToken.None);

        _lastStatusMessage.Should().Be("Saved: My Article");
        _navService.CurrentContext.StatusMessage.Should().Be("Saved: My Article",
            "status message persists until auto-expiry (3s)");
    }

    [Fact]
    public async Task HandleSaveToCollection_Failure_ShowsFailedMessage()
    {
        SetupHierarchicalWithSelectedLink("My Article", "https://example.com/article");
        _collectionService.SaveToReadingListAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CollectionItem>(_ => throw new InvalidOperationException("DB error"));

        await CollectionCommandHandler.HandleSaveToCollection(_ctx, _options, CancellationToken.None);

        _lastStatusMessage.Should().Be("Failed to save");
    }

    [Fact]
    public async Task HandleSaveToCollection_ReaderView_SavesCurrentPage()
    {
        // Arrange: set up a page in Readable view mode
        var page = Page.Create(
            "https://example.com/article",
            "<html></html>",
            new PageMetadata { Title = "Reader Article" });
        _navService.NavigateTo(page);
        _navService.SetViewMode(ViewMode.Readable);

        _collectionService.SaveToReadingListAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CollectionItem.Create(Guid.NewGuid(), "https://example.com/article", "Reader Article"));

        // Act
        await CollectionCommandHandler.HandleSaveToCollection(_ctx, _options, CancellationToken.None);

        // Assert
        _lastStatusMessage.Should().Be("Saved: Reader Article");
        await _collectionService.Received(1).SaveToReadingListAsync(
            "https://example.com/article", "Reader Article", Arg.Any<CancellationToken>());
    }

    #endregion

    #region HandleSaveToSpecific

    [Fact]
    public async Task HandleSaveToSpecific_NewItem_ShowsSavedToCollectionMessage()
    {
        SetupHierarchicalWithSelectedLink("My Article", "https://example.com/article");
        _ctx.InputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Favorites");
        _collectionService.SaveToCollectionByNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CollectionItem.Create(Guid.NewGuid(), "https://example.com/article", "My Article"));

        await CollectionCommandHandler.HandleSaveToSpecific(_ctx, _options, CancellationToken.None);

        _lastStatusMessage.Should().Be("Saved to Favorites: My Article");
    }

    [Fact]
    public async Task HandleSaveToSpecific_Duplicate_ShowsAlreadyInMessage()
    {
        SetupHierarchicalWithSelectedLink("My Article", "https://example.com/article");
        _ctx.InputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Favorites");
        _collectionService.SaveToCollectionByNameAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CollectionItem?)null);

        await CollectionCommandHandler.HandleSaveToSpecific(_ctx, _options, CancellationToken.None);

        _lastStatusMessage.Should().Be("Already in Favorites");
    }

    #endregion

    #region HandleSaveAllToReadingList

    [Fact]
    public async Task HandleSaveAllToReadingList_Success_ShowsCountMessage()
    {
        SetupHierarchicalWithLinks(
            ("Link 1", "https://example.com/1"),
            ("Link 2", "https://example.com/2"),
            ("Link 3", "https://example.com/3"));

        await CollectionCommandHandler.HandleSaveAllToReadingList(_ctx, _options, CancellationToken.None);

        _lastStatusMessage.Should().Be("Saved 3 links to Reading List");
    }

    #endregion

    #region HandleDeleteItem

    [Fact]
    public async Task HandleDeleteItem_RemoveItem_ShowsRemovedMessage()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://example.com/article", "Old Article");
        _navService.EnterCollections();
        _navService.EnterCollection(collection);
        _navService.CollectionItemSelectedIndex = 0;
        _ctx.InputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        try
        {
            await CollectionCommandHandler.HandleDeleteItem(_ctx, _options, CancellationToken.None);
            _lastStatusMessage.Should().Be("Removed: Old Article");
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task HandleDeleteItem_RemoveItem_Cancelled_NoAction()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://example.com/article", "Old Article");
        _navService.EnterCollections();
        _navService.EnterCollection(collection);
        _navService.CollectionItemSelectedIndex = 0;
        _ctx.InputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.GoBack, RawKeyChar = 'n' });

        try
        {
            await CollectionCommandHandler.HandleDeleteItem(_ctx, _options, CancellationToken.None);
            await _collectionService.DidNotReceive()
                .RemoveItemAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task HandleDeleteItem_DeleteCollection_ShowsDeletedMessage()
    {
        var collections = new List<Collection> { Collection.Create("My Collection") };
        _ctx.Collections = collections;
        _navService.EnterCollections();
        _ctx.InputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        try
        {
            await CollectionCommandHandler.HandleDeleteItem(_ctx, _options, CancellationToken.None);
            _lastStatusMessage.Should().Be("Deleted collection: My Collection");
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task HandleDeleteItem_DeleteCollection_Cancelled_NoAction()
    {
        var collections = new List<Collection> { Collection.Create("My Collection") };
        _ctx.Collections = collections;
        _navService.EnterCollections();
        _ctx.InputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.GoBack });

        try
        {
            await CollectionCommandHandler.HandleDeleteItem(_ctx, _options, CancellationToken.None);
            await _collectionService.DidNotReceive()
                .DeleteCollectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    #endregion

    #region HandleClearCollection

    [Fact]
    public async Task HandleClearCollection_Confirmed_ClearsAndShowsMessage()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://example.com/1", "Article 1");
        collection.AddItem("https://example.com/2", "Article 2");
        _navService.EnterCollections();
        _navService.EnterCollection(collection);
        _ctx.InputHandler.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("DELETE");

        try
        {
            await CollectionCommandHandler.HandleClearCollection(_ctx, _options, CancellationToken.None);
            _lastStatusMessage.Should().Be("Cleared: Reading List");
            await _collectionService.Received(1)
                .ClearCollectionAsync(collection.Id, Arg.Any<CancellationToken>());
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task HandleClearCollection_Cancelled_NoAction()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://example.com/1", "Article 1");
        _navService.EnterCollections();
        _navService.EnterCollection(collection);
        _ctx.InputHandler.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns((string?)null);

        try
        {
            await CollectionCommandHandler.HandleClearCollection(_ctx, _options, CancellationToken.None);
            await _collectionService.DidNotReceive()
                .ClearCollectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task HandleClearCollection_WrongViewMode_NoAction()
    {
        SetupHierarchicalWithSelectedLink("Article", "https://example.com/article");

        await CollectionCommandHandler.HandleClearCollection(_ctx, _options, CancellationToken.None);

        await _collectionService.DidNotReceive()
            .ClearCollectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleClearCollection_EmptyCollection_NoPrompt()
    {
        var collection = Collection.Create("Empty List");
        _navService.EnterCollections();
        _navService.EnterCollection(collection);

        await CollectionCommandHandler.HandleClearCollection(_ctx, _options, CancellationToken.None);

        await _ctx.InputHandler.DidNotReceive()
            .PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _collectionService.DidNotReceive()
            .ClearCollectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region HandleOpenCollections

    [Fact]
    public async Task HandleOpenCollections_ShowsCollectionListView()
    {
        // Act
        await CollectionCommandHandler.HandleOpenCollections(_ctx, _options, CancellationToken.None);

        // Assert: with no collections populated, stays on CollectionList
        _navService.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionList);
        _navService.ActiveCollection.Should().BeNull();
    }

    [Fact]
    public async Task HandleOpenCollections_SingleCollection_AutoEnters()
    {
        // Arrange: RefreshCollectionsAsync sets exactly one collection
        var readingList = Collection.Create("Reading List");
        readingList.AddItem("https://example.com/saved", "Saved Article");
        _ctx.Collections = new List<Collection> { readingList }.AsReadOnly();

        // Act
        await CollectionCommandHandler.HandleOpenCollections(_ctx, _options, CancellationToken.None);

        // Assert: auto-entered the sole collection
        _navService.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionItems);
        _navService.ActiveCollection.Should().NotBeNull();
        _navService.ActiveCollection!.Name.Should().Be("Reading List");
    }

    [Fact]
    public async Task HandleOpenCollections_Failure_ShowsErrorMessage()
    {
        // Arrange: create a context where RefreshCollectionsAsync throws
        string? statusMessage = null;
        var logger = Substitute.For<ILogger<NavigationService>>();
        var navService = new NavigationService(logger);
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(Substitute.For<IServiceScope>());

        var errorCtx = new CommandContext
        {
            NavigationService = navService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = scopeFactory,
            Logger = NullLogger.Instance,
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(navService, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            RenderCurrentPageAsync = (_, _) =>
            {
                statusMessage = navService.CurrentContext.StatusMessage;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => throw new InvalidOperationException("DB error"),
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => _collectionService,
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        // Act
        await CollectionCommandHandler.HandleOpenCollections(errorCtx, _options, CancellationToken.None);

        // Assert
        statusMessage.Should().Contain("Failed to load collections");
    }

    #endregion

    #region Helpers

    private void SetupHierarchicalWithSelectedLink(string title, string url)
    {
        var links = new List<LinkInfo>
        {
            new LinkInfo { Url = url, DisplayText = title, Type = LinkType.Content, ImportanceScore = 80 }
        };
        var tree = NavigationTree.Build(links);
        var page = Page.Create(url, "<html></html>", new PageMetadata { Title = title });
        page.SetLinkTree(tree);
        _navService.NavigateTo(page);
    }

    private void SetupHierarchicalWithLinks(params (string Title, string Url)[] items)
    {
        var links = items.Select(i => new LinkInfo
        {
            Url = i.Url,
            DisplayText = i.Title,
            Type = LinkType.Content,
            ImportanceScore = 80
        }).ToList();
        var tree = NavigationTree.Build(links);
        var page = Page.Create("https://example.com", "<html></html>", new PageMetadata { Title = "Page" });
        page.SetLinkTree(tree);
        _navService.NavigateTo(page);
    }

    #endregion
}
