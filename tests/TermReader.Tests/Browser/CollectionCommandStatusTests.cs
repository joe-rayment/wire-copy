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
            RenderCurrentPageAsync = (_, _) =>
            {
                _lastStatusMessage = _navService.CurrentContext.StatusMessage;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
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

        await CollectionCommandHandler.HandleDeleteItem(_ctx, _options, CancellationToken.None);

        _lastStatusMessage.Should().Be("Removed: Old Article");
    }

    [Fact]
    public async Task HandleDeleteItem_DeleteCollection_ShowsDeletedMessage()
    {
        var collections = new List<Collection> { Collection.Create("My Collection") };
        _ctx.Collections = collections;
        _navService.EnterCollections();
        // Stay in CollectionList view (don't enter a specific collection)

        await CollectionCommandHandler.HandleDeleteItem(_ctx, _options, CancellationToken.None);

        _lastStatusMessage.Should().Be("Deleted collection: My Collection");
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
