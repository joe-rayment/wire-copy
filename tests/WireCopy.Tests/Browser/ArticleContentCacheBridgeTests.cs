// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the article content cache bridge in BrowserOrchestrator.
/// When navigating from a collection (Reading List), the orchestrator should check
/// IArticleContentCache before doing any network I/O.
/// </summary>
[Trait("Category", "Unit")]
public class ArticleContentCacheBridgeTests
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly NavigationService _navigationService;
    private readonly IArticleContentCache _articleContentCache;
    private readonly BrowserOrchestrator _sut;

    public ArticleContentCacheBridgeTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _linkExtractor = Substitute.For<ILinkExtractor>();
        _treeBuilder = Substitute.For<INavigationTreeBuilder>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _renderer = Substitute.For<IPageRenderer>();
        _articleContentCache = Substitute.For<IArticleContentCache>();

        var inputHandler = Substitute.For<IInputHandler>();
        var browserConfig = Options.Create(new BrowserConfiguration());
        var logger = Substitute.For<ILogger<BrowserOrchestrator>>();
        var navLogger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(navLogger);

        // Set up DI scope that provides IArticleContentCache
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        var collectionService = Substitute.For<ICollectionService>();
        collectionService.GetAllCollectionsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Domain.Entities.Collections.Collection>>(
                new List<Domain.Entities.Collections.Collection>()));
        collectionService.GetDefaultCollectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Domain.Entities.Collections.Collection.Create("Reading List")));

        var bookmarkService = Substitute.For<IBookmarkService>();
        bookmarkService.GetAllBookmarksAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Domain.Entities.Bookmarks.Bookmark>>(
                new List<Domain.Entities.Bookmarks.Bookmark>()));

        serviceProvider.GetService(typeof(ICollectionService)).Returns(collectionService);
        serviceProvider.GetService(typeof(IBookmarkService)).Returns(bookmarkService);
        serviceProvider.GetService(typeof(IArticleContentCache)).Returns(_articleContentCache);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        var browserSession = Substitute.For<IBrowserSessionControl>();
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var resizeDetector = Substitute.For<IResizeDetector>();

        _treeBuilder.BuildTreeAsync(Arg.Any<List<LinkInfo>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => NavigationTree.Build(callInfo.ArgAt<List<LinkInfo>>(0)));

        var pageCache = Substitute.For<IPageCache>();
        var preloadService = Substitute.For<IPreloadService>();

        var pipeline = BrowserOrchestratorTestHelper.CreatePipeline(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            _navigationService,
            scopeFactory,
            browserSession,
            pageCache,
            preloadService);

        _sut = new BrowserOrchestrator(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            inputHandler,
            _navigationService,
            scopeFactory,
            browserSession,
            themeProvider,
            resizeDetector,
            pageCache,
            preloadService,
            Substitute.For<IIdleDetector>(),
            Substitute.For<ICookieManager>(),
            Substitute.For<IHttpCookieRefresher>(),
            browserConfig,
            logger,
            pipeline,
            Substitute.For<ILayoutVariantProvider>(),
            BrowserOrchestratorTestHelper.CreateDockSpotlight(),
            Substitute.For<IWebPaneSink>());
    }

    /// <summary>
    /// Simulates collection navigation context by entering a collection and saving a return point.
    /// </summary>
    private void SimulateCollectionContext()
    {
        var collection = Domain.Entities.Collections.Collection.Create("Reading List");
        collection.AddItem("https://example.com/article", "Test Article");
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);

        // Create a dummy page so SaveCollectionReturnPoint works
        var dummyHtml = "<html><body>Dummy</body></html>";
        var dummyPage = Page.Create("https://example.com", dummyHtml, new PageMetadata { Title = "Dummy" });
        dummyPage.SetLinkTree(NavigationTree.Build(new List<LinkInfo>()));
        _navigationService.NavigateTo(dummyPage);

        _navigationService.SaveCollectionReturnPoint();
    }

    private void SetupNormalPageLoad(string url, string title = "Network Page")
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body><p>Content</p></body></html>";

        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(url, html, metadata));

        _linkExtractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LinkInfo>());

        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReadableContent.Create(
                title,
                "Network-loaded content with enough words here.",
                new List<string> { "Network-loaded content with enough words here." }));
    }

    #region Article cache hit from collection

    [Fact]
    public async Task LoadPageAsync_CollectionContext_ArticleCacheHit_SkipsNetworkLoad()
    {
        // Arrange
        SimulateCollectionContext();
        var url = "https://example.com/article";

        _articleContentCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Cached Article",
                CleanedText = "This is cached article content from podcast extraction.",
                Author = "Test Author",
                Url = url,
                WordCount = 8,
            });

        SetupNormalPageLoad(url); // set up in case cache is missed

        // Act
        var page = await _sut.LoadPageAsync(url);

        // Assert — page should be built from cache, not network
        page.Should().NotBeNull();
        page.Url.Should().Be(url);
        page.Metadata.Title.Should().Be("Cached Article");
        page.HasReadableContent().Should().BeTrue();
        page.ReadableContent!.Title.Should().Be("Cached Article");
        page.ReadableContent.CleanedText.Should().Contain("cached article content");
        page.ReadableContent.Author.Should().Be("Test Author");

        // IPageLoader should NOT have been called
        await _pageLoader.DidNotReceive().LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadPageAsync_CollectionContext_ArticleCacheHit_DoesNotShowLoadingScreen()
    {
        // Arrange
        SimulateCollectionContext();
        var url = "https://example.com/article";

        _articleContentCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Cached Article",
                CleanedText = "Cached content text.",
                Url = url,
                WordCount = 3,
            });

        // Act
        await _sut.LoadPageAsync(url);

        // Assert — loading screen should not be shown for instant cache hit
        _renderer.DidNotReceive().RenderLoading(Arg.Any<string>());
    }

    [Fact]
    public async Task LoadPageAsync_CollectionContext_ArticleCacheHit_PreservesAuthorAndDate()
    {
        // Arrange
        SimulateCollectionContext();
        var url = "https://example.com/article";
        var pubDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        _articleContentCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Article With Metadata",
                CleanedText = "Content with author and date metadata preserved.",
                Author = "Jane Doe",
                Url = url,
                WordCount = 7,
                PublishedDate = pubDate,
            });

        // Act
        var page = await _sut.LoadPageAsync(url);

        // Assert
        page.ReadableContent!.Author.Should().Be("Jane Doe");
        page.ReadableContent.PublishedDate.Should().Be(pubDate);
    }

    #endregion

    #region Article cache miss — falls through to network

    [Fact]
    public async Task LoadPageAsync_CollectionContext_ArticleCacheMiss_FallsThroughToNetwork()
    {
        // Arrange
        SimulateCollectionContext();
        var url = "https://example.com/article";

        _articleContentCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);

        SetupNormalPageLoad(url, "Network Article");

        // Act
        var page = await _sut.LoadPageAsync(url);

        // Assert — page should come from network
        page.Should().NotBeNull();
        page.Metadata.Title.Should().Be("Network Article");

        // IPageLoader should have been called since cache missed
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region No collection context — article cache not consulted

    [Fact]
    public async Task LoadPageAsync_NoCollectionContext_DoesNotCheckArticleCache()
    {
        // Arrange — no SimulateCollectionContext(), so HasCollectionReturnPoint is false
        var url = "https://example.com/article";
        SetupNormalPageLoad(url);

        // Act
        var page = await _sut.LoadPageAsync(url);

        // Assert — article cache should not have been consulted
        await _articleContentCache.DidNotReceive().TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Page should come from network
        page.Metadata.Title.Should().Be("Network Page");
    }

    #endregion

    #region Article cache error — graceful fallthrough

    [Fact]
    public async Task LoadPageAsync_CollectionContext_ArticleCacheThrows_FallsThroughToNetwork()
    {
        // Arrange
        SimulateCollectionContext();
        var url = "https://example.com/article";

        _articleContentCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns<ExtractedArticle?>(_ => throw new IOException("Cache file corrupted"));

        SetupNormalPageLoad(url, "Fallback Network Page");

        // Act
        var page = await _sut.LoadPageAsync(url);

        // Assert — should gracefully fall through to network
        page.Should().NotBeNull();
        page.Metadata.Title.Should().Be("Fallback Network Page");
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Paragraph splitting

    [Fact]
    public async Task LoadPageAsync_CollectionContext_ArticleCacheHit_SplitsParagraphs()
    {
        // Arrange
        SimulateCollectionContext();
        var url = "https://example.com/article";

        _articleContentCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Multi-Paragraph Article",
                CleanedText = "First paragraph content.\n\nSecond paragraph content.\n\nThird paragraph.",
                Url = url,
                WordCount = 6,
            });

        // Act
        var page = await _sut.LoadPageAsync(url);

        // Assert — paragraphs should be split on double newlines
        page.ReadableContent!.Paragraphs.Should().HaveCount(3);
        page.ReadableContent.Paragraphs[0].Should().Be("First paragraph content.");
        page.ReadableContent.Paragraphs[1].Should().Be("Second paragraph content.");
        page.ReadableContent.Paragraphs[2].Should().Be("Third paragraph.");
    }

    #endregion
}
