// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

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
            Substitute.For<IPreloadService>(),
            Substitute.For<IIdleDetector>(),
            Substitute.For<ICookieManager>(),
            browserConfig,
            logger);
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
    public async Task LoadPageAsync_CacheMiss_ShowsLoadingScreen()
    {
        _pageCache.Contains("https://example.com").Returns(false);
        SetupPageLoad("https://example.com");

        await _sut.LoadPageAsync("https://example.com");

        _renderer.Received().RenderLoading("https://example.com");
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
    public async Task LoadPageAsync_HttpNoContent_ShowsLoadingScreenOnRetry()
    {
        var url = "https://example.com/article";
        _pageCache.Contains(url).Returns(true);
        SetupPageLoadNoContent(url, FetchMethod.Http);

        await _sut.LoadPageAsync(url);

        // Cache hit skips initial loading screen, but fallback shows it
        _renderer.Received(1).RenderLoading(url);
    }

    [Fact]
    public async Task LoadPageAsync_SeleniumNoContent_DoesNotRetry()
    {
        var url = "https://example.com/article";
        SetupPageLoadNoContent(url, FetchMethod.Selenium);

        await _sut.LoadPageAsync(url);

        // Only one call — no ForceRefresh retry when already Selenium
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
        SetupPageLoad(url, fetchMethod: FetchMethod.Http, hasReadableContent: false);

        // First call returns null, second call returns content
        var readable = ReadableContent.Create(
            "Real Article",
            "This is the actual article content from Selenium.",
            new List<string> { "This is the actual article content from Selenium." });
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
            FetchMethod.Selenium);
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
        SetupPageLoad(url, fetchMethod: FetchMethod.Http, hasReadableContent: false);

        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        // Retry fails
        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.ForceRefresh),
            Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Failure("Selenium timeout"));

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
        SetupPageLoad(url, fetchMethod: fetchMethod, hasReadableContent: false);

        // Override content extractor to return null (no readable content)
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        // Set up retry result for ForceRefresh fallback
        var retryResult = PageLoadResult.Successful(
            url,
            "<html><body><p>Retry content</p></body></html>",
            new PageMetadata { Title = "Retry" },
            FetchMethod.Selenium);
        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.ForceRefresh),
            Arg.Any<CancellationToken>())
            .Returns(retryResult);
    }

    private void SetupPageLoad(
        string url,
        string title = "Test Page",
        FetchMethod fetchMethod = FetchMethod.Http,
        bool hasReadableContent = true)
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body>Content</body></html>";

        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => !r.ForceRefresh),
            Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(url, html, metadata, fetchMethod));

        var links = new List<LinkInfo>
        {
            new LinkInfo { Url = $"{url}/link1", DisplayText = "Link One", Type = LinkType.Content, ImportanceScore = 80 }
        };

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
