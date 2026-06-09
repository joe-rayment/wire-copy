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
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class CacheServingFlowTests
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly IPageCache _pageCache;
    private readonly BrowserOrchestrator _sut;

    public CacheServingFlowTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _linkExtractor = Substitute.For<ILinkExtractor>();
        _treeBuilder = Substitute.For<INavigationTreeBuilder>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _renderer = Substitute.For<IPageRenderer>();
        _pageCache = Substitute.For<IPageCache>();

        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.IsInteractive.Returns(true);
        var browserConfig = Options.Create(new BrowserConfiguration());
        var logger = Substitute.For<ILogger<BrowserOrchestrator>>();
        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(navLogger);

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
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var resizeDetector = Substitute.For<IResizeDetector>();

        var preloadService = Substitute.For<IPreloadService>();

        var pipeline = BrowserOrchestratorTestHelper.CreatePipeline(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            navigationService,
            scopeFactory,
            browserSession,
            _pageCache,
            preloadService);

        _sut = new BrowserOrchestrator(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            inputHandler,
            navigationService,
            scopeFactory,
            browserSession,
            themeProvider,
            resizeDetector,
            _pageCache,
            preloadService,
            Substitute.For<IIdleDetector>(),
            Substitute.For<ICookieManager>(),
            Substitute.For<IHttpCookieRefresher>(),
            browserConfig,
            logger,
            pipeline,
            Substitute.For<ILayoutVariantProvider>(),
            BrowserOrchestratorTestHelper.CreateDockSpotlight());
    }

    #region Loading screen skip for cache hits

    [Fact]
    public async Task LoadPageAsync_CacheHit_SkipsLoadingScreen()
    {
        _pageCache.Contains("https://example.com").Returns(true);
        SetupPageLoad("https://example.com");

        await _sut.LoadPageAsync("https://example.com");

        _renderer.DidNotReceive().RenderLoading(Arg.Any<string>());
    }

    [Fact]
    public async Task LoadPageAsync_CacheMiss_DoesNotShowBlockingLoadingScreen()
    {
        _pageCache.Contains("https://example.com").Returns(false);
        SetupPageLoad("https://example.com");

        await _sut.LoadPageAsync("https://example.com");

        // Pipeline no longer calls RenderLoading — loading status is communicated
        // through the status bar (progressive loading)
        _renderer.DidNotReceive().RenderLoading(Arg.Any<string>());
    }

    #endregion

    #region Content-quality fallback

    [Fact]
    public async Task LoadPageAsync_HttpNoContent_RetriesWithForceRefresh()
    {
        var url = "https://example.com/article";
        SetupPageLoadNoContent(url, FetchMethod.Http);

        await _sut.LoadPageAsync(url);

        // Should have called LoadAsync twice: initial + retry with ForceRefresh
        await _pageLoader.Received(2).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
        await _pageLoader.Received(1).LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.ForceRefresh),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadPageAsync_HttpNoContent_RemovesCacheEntry()
    {
        var url = "https://example.com/article";
        SetupPageLoadNoContent(url, FetchMethod.Http);

        await _sut.LoadPageAsync(url);

        _pageCache.Received(1).Remove(url);
    }

    [Fact]
    public async Task LoadPageAsync_HttpNoContent_DoesNotShowBlockingLoadingScreen()
    {
        var url = "https://example.com/article";
        _pageCache.Contains(url).Returns(true);
        SetupPageLoadNoContent(url, FetchMethod.Http);

        await _sut.LoadPageAsync(url);

        // Pipeline no longer calls RenderLoading — status communicated via status bar
        _renderer.DidNotReceive().RenderLoading(Arg.Any<string>());
    }

    [Fact]
    public async Task LoadPageAsync_BrowserNoContent_DoesNotRetry()
    {
        var url = "https://example.com/article";
        SetupPageLoadNoContent(url, FetchMethod.Browser);

        await _sut.LoadPageAsync(url);

        // Only one call — no ForceRefresh retry when already Browser
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadPageAsync_CachedNoContent_RetriesWithForceRefresh()
    {
        var url = "https://example.com/article";
        SetupPageLoadNoContent(url, FetchMethod.Cached);

        await _sut.LoadPageAsync(url);

        // Cached content with no readable text should trigger fallback
        await _pageLoader.Received(1).LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.ForceRefresh),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadPageAsync_HttpNoContent_RetrySucceeds_SetsReadableContent()
    {
        var url = "https://example.com/article";
        SetupPageLoad(url, fetchMethod: FetchMethod.Http, hasReadableContent: false, hasLinks: false);

        // First call returns null, second call returns content
        var readable = ReadableContent.Create(
            "Real Article",
            "This is the actual article content from Browser.",
            new List<string> { "This is the actual article content from Browser." });
        var callCount = 0;
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? null : readable;
            });

        var retryResult = PageLoadResult.Successful(
            url, "<html><body><p>Real content</p></body></html>",
            new PageMetadata { Title = "Real Article" },
            FetchMethod.Browser);
        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.ForceRefresh),
            Arg.Any<CancellationToken>())
            .Returns(retryResult);

        var page = await _sut.LoadPageAsync(url);

        page.HasReadableContent().Should().BeTrue();
        page.ReadableContent!.Title.Should().Be("Real Article");
    }

    [Fact]
    public async Task LoadPageAsync_HttpNoContent_RetryFails_ReturnsPageWithoutContent()
    {
        var url = "https://example.com/article";
        SetupPageLoad(url, fetchMethod: FetchMethod.Http, hasReadableContent: false, hasLinks: false);

        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        // Retry fails
        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.ForceRefresh),
            Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Failure("Browser timeout"));

        var page = await _sut.LoadPageAsync(url);

        page.Should().NotBeNull();
        page.HasReadableContent().Should().BeFalse();
    }

    [Fact]
    public async Task LoadPageAsync_HttpWithContent_DoesNotRetry()
    {
        var url = "https://example.com/article";
        SetupPageLoad(url, fetchMethod: FetchMethod.Http);

        await _sut.LoadPageAsync(url);

        // Content found — no retry needed
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Helpers

    private void SetupPageLoadNoContent(string url, FetchMethod fetchMethod)
    {
        SetupPageLoad(url, fetchMethod: fetchMethod, hasReadableContent: false, hasLinks: false);

        // Override content extractor to return null (no readable content)
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        // Set up retry result for ForceRefresh fallback
        var retryResult = PageLoadResult.Successful(
            url,
            "<html><body><p>Retry content</p></body></html>",
            new PageMetadata { Title = "Retry" },
            FetchMethod.Browser);
        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.ForceRefresh),
            Arg.Any<CancellationToken>())
            .Returns(retryResult);
    }

    private void SetupPageLoad(
        string url,
        string title = "Test Page",
        FetchMethod fetchMethod = FetchMethod.Http,
        bool hasReadableContent = true,
        bool hasLinks = true)
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body>Content</body></html>";

        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => !r.ForceRefresh),
            Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(url, html, metadata, fetchMethod));

        var links = hasLinks
            ? new List<LinkInfo>
            {
                new LinkInfo { Url = $"{url}/link1", DisplayText = "Link One", Type = LinkType.Content, ImportanceScore = 80 },
            }
            : new List<LinkInfo>();

        _linkExtractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(links);

        var tree = NavigationTree.Build(links);
        _treeBuilder.BuildTreeAsync(Arg.Any<List<LinkInfo>>(), Arg.Any<CancellationToken>())
            .Returns(tree);

        if (hasReadableContent)
        {
            var readable = ReadableContent.Create(
                "Article Title",
                "Some article content here with enough words for a test.",
                new List<string> { "Paragraph one content.", "Paragraph two content." });
            _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(readable);
        }
    }

    #endregion
}
