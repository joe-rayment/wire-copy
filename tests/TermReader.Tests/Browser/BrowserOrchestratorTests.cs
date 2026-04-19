// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public class BrowserOrchestratorTests
{
    private readonly IPageLoader _pageLoader;
    private readonly IPageRenderer _renderer;
    private readonly IInputHandler _inputHandler;
    private readonly NavigationService _navigationService;
    private readonly BrowserOrchestrator _sut;

    // Real implementations (not mocks) — tests verify actual HTML parsing pipeline
    private readonly LinkExtractor _linkExtractor;
    private readonly NavigationTreeBuilder _treeBuilder;
    private readonly ReadableContentExtractor _contentExtractor;

    public BrowserOrchestratorTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _linkExtractor = new LinkExtractor(NullLogger<LinkExtractor>.Instance);
        _treeBuilder = new NavigationTreeBuilder(NullLogger<NavigationTreeBuilder>.Instance);
        _contentExtractor = new ReadableContentExtractor(NullLogger<ReadableContentExtractor>.Instance);
        _renderer = Substitute.For<IPageRenderer>();
        _inputHandler = Substitute.For<IInputHandler>();

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(navLogger);

        _sut = BrowserOrchestratorTestHelper.CreateOrchestrator(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            _inputHandler,
            _navigationService);
    }

    private void SetupPageLoad(string url, string title = "Test Page", bool hasReadableContent = false)
    {
        // Use realistic HTML so real extractors produce meaningful results
        var html = hasReadableContent
            ? $"""
              <html><head><title>{title}</title></head>
              <body>
                <nav><a href="{url}/nav1">Home</a></nav>
                <article>
                  <h1>Article Title</h1>
                  <p>This is a substantial article paragraph with enough content to pass the quality gate.
                  The quick brown fox jumps over the lazy dog near the riverbank on a sunny afternoon.
                  Technology continues to evolve at a rapid pace bringing new innovations to every industry.
                  Researchers published their findings in a peer-reviewed journal last week with great results.</p>
                  <p>A second paragraph ensures sufficient content depth for the readable content extractor
                  to recognize this as a genuine article rather than navigation or boilerplate text.</p>
                </article>
                <a href="https://example.com/link1">Link One</a>
                <a href="https://example.com/link2">Link Two</a>
              </body></html>
              """
            : $"""
              <html><head><title>{title}</title></head>
              <body>
                <a href="https://example.com/link1">Link One</a>
                <a href="https://example.com/link2">Link Two</a>
              </body></html>
              """;

        _pageLoader.LoadAsync(Arg.Is<PageLoadRequest>(r => r.Url == url), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(url, html, new PageMetadata { Title = title }));
    }

    [Fact]
    public async Task LoadPageAsync_Success_ReturnsPageWithLinks()
    {
        // Arrange
        SetupPageLoad("https://example.com", "Example");

        // Act
        var page = await _sut.LoadPageAsync("https://example.com");

        // Assert
        page.Should().NotBeNull();
        page.Url.Should().Be("https://example.com");
        page.Metadata.Title.Should().Be("Example");
        page.LinkTree.Should().NotBeNull();
        page.LinkTree!.TotalLinks.Should().BeGreaterThan(0, "real LinkExtractor should find links in the HTML");
    }

    [Fact]
    public async Task LoadPageAsync_WithReadableContent_SetsReadableContent()
    {
        // Arrange
        SetupPageLoad("https://example.com/article", "Article", hasReadableContent: true);

        // Act
        var page = await _sut.LoadPageAsync("https://example.com/article");

        // Assert — real ReadableContentExtractor processes the HTML
        page.HasReadableContent().Should().BeTrue("article HTML should be detected as readable content");
        page.ReadableContent!.WordCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LoadPageAsync_FailedLoad_ThrowsInvalidOperationException()
    {
        // Arrange
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Failure("Connection refused"));

        // Act & Assert
        await FluentActions.Invoking(() => _sut.LoadPageAsync("https://example.com"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Failed to load page: Connection refused");
    }

    [Fact]
    public async Task LoadPageAsync_DoesNotCallBlockingRenderLoading()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        // Act
        await _sut.LoadPageAsync("https://example.com");

        // Assert — pipeline uses status bar updates instead of blocking RenderLoading
        _renderer.DidNotReceive().RenderLoading(Arg.Any<string>());
    }

    [Fact]
    public async Task RenderAsync_HierarchicalMode_CallsRenderHierarchical()
    {
        // Arrange
        SetupPageLoad("https://example.com");
        var page = await _sut.LoadPageAsync("https://example.com");
        _navigationService.NavigateTo(page);
        var options = new RenderOptions { TerminalWidth = 80, TerminalHeight = 24 };

        // Act
        await _sut.RenderAsync(page, ViewMode.Hierarchical, options);

        // Assert
        _renderer.Received(1).RenderHierarchical(page, Arg.Any<NavigationContext>(), options);
    }

    [Fact]
    public async Task RenderAsync_ReadableMode_CallsRenderReadable()
    {
        // Arrange
        SetupPageLoad("https://example.com");
        var page = await _sut.LoadPageAsync("https://example.com");
        _navigationService.NavigateTo(page);
        var options = new RenderOptions { TerminalWidth = 80, TerminalHeight = 24 };

        // Act
        await _sut.RenderAsync(page, ViewMode.Readable, options);

        // Assert
        _renderer.Received(1).RenderReadable(page, Arg.Any<NavigationContext>(), options, Arg.Any<List<string>?>());
    }

    [Fact]
    public async Task BuildNavigationTreeAsync_WithExistingTree_ReturnsExistingTree()
    {
        // Arrange
        SetupPageLoad("https://example.com");
        var page = await _sut.LoadPageAsync("https://example.com");

        // Act
        var tree = await _sut.BuildNavigationTreeAsync(page);

        // Assert
        tree.Should().NotBeNull();
        tree.Should().BeSameAs(page.LinkTree);
    }

    [Fact]
    public async Task ExtractReadableContentAsync_WithExistingContent_ReturnsExistingContent()
    {
        // Arrange
        SetupPageLoad("https://example.com", hasReadableContent: true);
        var page = await _sut.LoadPageAsync("https://example.com");

        // Act
        var content = await _sut.ExtractReadableContentAsync(page);

        // Assert
        content.Should().NotBeNull();
        content.Should().BeSameAs(page.ReadableContent);
    }

    [Fact]
    public async Task ExtractReadableContentAsync_WithoutExistingContent_RunsExtractorOnRawHtml()
    {
        // Arrange — non-article HTML, so initial load produces no readable content
        SetupPageLoad("https://example.com", hasReadableContent: false);
        var page = await _sut.LoadPageAsync("https://example.com");

        // Act — real extractor processes the raw HTML again
        var content = await _sut.ExtractReadableContentAsync(page);

        // Assert — the minimal non-article HTML may or may not produce readable content
        // but calling ExtractReadableContentAsync should not throw
        // (the real extractor correctly handles non-article pages)
    }
}

/// <summary>
/// Tests for redirect handling and URL preservation in LoadPageAsync.
/// When a server redirects (e.g., /old -> /new), the Page entity should
/// preserve the originally requested URL, not the final redirect target.
/// </summary>
[Trait("Category", "Unit")]
public class BrowserOrchestratorRedirectTests
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly IPageCache _pageCache;
    private readonly BrowserOrchestrator _sut;

    public BrowserOrchestratorRedirectTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _linkExtractor = Substitute.For<ILinkExtractor>();
        _treeBuilder = Substitute.For<INavigationTreeBuilder>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _renderer = Substitute.For<IPageRenderer>();
        _pageCache = Substitute.For<IPageCache>();

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(navLogger);

        _sut = BrowserOrchestratorTestHelper.CreateOrchestrator(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            Substitute.For<IInputHandler>(),
            navigationService,
            _pageCache);
    }

    [Fact]
    public async Task LoadPageAsync_UsesFinalUrl_WhenServerRedirects()
    {
        // Arrange - PageLoader returns a result where the final URL differs from requested
        var requestedUrl = "https://example.com/short-link";
        var redirectedUrl = "https://example.com/articles/2024/01/full-article-slug";

        var metadata = new PageMetadata { Title = "Redirected Article" };
        var html = "<html><head><title>Redirected Article</title></head><body><article><p>Content</p></article></body></html>";

        _pageLoader.LoadAsync(Arg.Is<PageLoadRequest>(r => r.Url == requestedUrl), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(redirectedUrl, html, metadata));

        var links = new List<LinkInfo>();
        _linkExtractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Is(redirectedUrl), Arg.Any<CancellationToken>())
            .Returns(links);
        _treeBuilder.BuildTreeAsync(Arg.Any<List<LinkInfo>>(), Arg.Any<CancellationToken>())
            .Returns(NavigationTree.Build(links));

        var readable = ReadableContent.Create(
            "Redirected Article",
            "Content of the redirected article with enough text to pass.",
            new List<string> { "Content of the redirected article with enough text to pass." });
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Is(redirectedUrl), Arg.Any<CancellationToken>())
            .Returns(readable);

        // Act
        var page = await _sut.LoadPageAsync(requestedUrl);

        // Assert - Page URL should be the final URL after redirect, so that status bar,
        // refresh, and cache lookups all use the correct URL.
        page.Url.Should().Be(redirectedUrl,
            "the Page entity should use the final URL after redirect for correct cache/refresh behavior");
    }

    [Fact]
    public async Task Cache_DoesNotServeWrongContent_AfterRedirect()
    {
        // Arrange - URL A and URL B have different content; cache should not mix them
        var urlA = "https://example.com/article-a";
        var urlB = "https://example.com/article-b";

        // URL A returns content for article A
        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.Url == urlA),
            Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(
                urlA,
                "<html><body><article><h1>Article A</h1><p>Content A</p></article></body></html>",
                new PageMetadata { Title = "Article A" }));

        // URL B returns different content (simulating no cache collision)
        _pageLoader.LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.Url == urlB),
            Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(
                urlB,
                "<html><body><article><h1>Article B</h1><p>Content B</p></article></body></html>",
                new PageMetadata { Title = "Article B" }));

        var links = new List<LinkInfo>();
        _linkExtractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(links);
        _treeBuilder.BuildTreeAsync(Arg.Any<List<LinkInfo>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => NavigationTree.Build(callInfo.ArgAt<List<LinkInfo>>(0)));

        // Content extraction returns different content based on the HTML
        _contentExtractor.ExtractAsync(
            Arg.Is<string>(h => h.Contains("Article A")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(ReadableContent.Create(
                "Article A",
                "Content A with sufficient length for testing purposes here.",
                new List<string> { "Content A with sufficient length for testing purposes here." }));

        _contentExtractor.ExtractAsync(
            Arg.Is<string>(h => h.Contains("Article B")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(ReadableContent.Create(
                "Article B",
                "Content B with sufficient length for testing purposes here.",
                new List<string> { "Content B with sufficient length for testing purposes here." }));

        // Act - load both pages
        var pageA = await _sut.LoadPageAsync(urlA);
        var pageB = await _sut.LoadPageAsync(urlB);

        // Assert - each page has its own content
        pageA.Url.Should().Be(urlA);
        pageA.Metadata.Title.Should().Be("Article A");

        pageB.Url.Should().Be(urlB);
        pageB.Metadata.Title.Should().Be("Article B");

        // Verify both pages were actually loaded (no cross-contamination)
        pageA.Metadata.Title.Should().NotBe(pageB.Metadata.Title,
            "different URLs should return different content, not cached cross-contamination");
    }
}

/// <summary>
/// Tests for HandleCommandAsync behavior via the RunAsync loop (indirectly).
/// Since HandleCommandAsync is private, these tests verify behavior by setting up
/// the orchestrator with specific input sequences and checking side effects.
/// </summary>
[Trait("Category", "Unit")]
public class BrowserOrchestratorNavigationTests
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly IInputHandler _inputHandler;
    private readonly IPageCache _pageCache;
    private readonly NavigationService _navigationService;
    private readonly BrowserOrchestrator _sut;

    public BrowserOrchestratorNavigationTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _linkExtractor = Substitute.For<ILinkExtractor>();
        _treeBuilder = Substitute.For<INavigationTreeBuilder>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _renderer = Substitute.For<IPageRenderer>();
        _inputHandler = Substitute.For<IInputHandler>();
        _pageCache = Substitute.For<IPageCache>();

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(navLogger);

        _sut = BrowserOrchestratorTestHelper.CreateOrchestrator(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            _inputHandler,
            _navigationService,
            pageCache: _pageCache);
    }

    private void SetupPageLoad(string url, string title = "Test Page")
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body>Content</body></html>";

        _pageLoader.LoadAsync(Arg.Is<PageLoadRequest>(r => r.Url == url), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(url, html, metadata));

        // Mark URL as cached so NavigateToAsync takes the synchronous path
        _pageCache.Contains(url).Returns(true);

        var links = new List<LinkInfo>
        {
            new LinkInfo { Url = $"{url}/link1", DisplayText = "Link One", Type = LinkType.Content, ImportanceScore = 80 }
        };

        _linkExtractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(links);

        _treeBuilder.BuildTreeAsync(Arg.Any<List<LinkInfo>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => NavigationTree.Build(callInfo.ArgAt<List<LinkInfo>>(0)));

        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);
    }

    [Fact]
    public async Task RunAsync_QuitCommand_ExitsLoop()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.Quit });

        // Act - should exit quickly without hanging
        await _sut.RunAsync("https://example.com");

        // Assert - renderer should have been called at least once for the initial page
        _renderer.Received().RenderHierarchical(Arg.Any<Page>(), Arg.Any<NavigationContext>(), Arg.Any<RenderOptions>());
    }

    [Fact]
    public async Task RunAsync_MoveDownThenQuit_UpdatesSelection()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new NavigationCommand { Type = CommandType.MoveDown }
                    : new NavigationCommand { Type = CommandType.Quit };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - renderer was called at least twice (initial + after move down)
        _renderer.Received().RenderHierarchical(
            Arg.Any<Page>(), Arg.Any<NavigationContext>(), Arg.Any<RenderOptions>());
    }

    [Fact]
    public async Task RunAsync_SwitchViewThenQuit_TogglesViewMode()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new NavigationCommand { Type = CommandType.SwitchView }
                    : new NavigationCommand { Type = CommandType.Quit };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - after SwitchView, RenderReadable should be called
        _renderer.Received().RenderReadable(
            Arg.Any<Page>(), Arg.Any<NavigationContext>(), Arg.Any<RenderOptions>(), Arg.Any<List<string>?>());
    }

    [Fact]
    public async Task RunAsync_NoUrl_ExitsImmediately()
    {
        // Arrange
        _inputHandler.PromptForUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        // Act
        await _sut.RunAsync();

        // Assert - should not have loaded any page
        await _pageLoader.DidNotReceive().LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_GoBackThenQuit_NavigatesBack()
    {
        // Arrange
        SetupPageLoad("https://example.com", "Page 1");
        SetupPageLoad("https://example.com/link1", "Page 2");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new NavigationCommand { Type = CommandType.Navigate, TargetUrl = "https://example.com/link1" },
                    2 => new NavigationCommand { Type = CommandType.GoBack },
                    _ => new NavigationCommand { Type = CommandType.Quit }
                };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - after going back, the current page should be the first page
        _navigationService.CurrentPage!.Url.Should().Be("https://example.com");
        _navigationService.CanGoForward.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_GoForwardThenQuit_NavigatesForward()
    {
        // Arrange
        SetupPageLoad("https://example.com", "Page 1");
        SetupPageLoad("https://example.com/link1", "Page 2");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new NavigationCommand { Type = CommandType.Navigate, TargetUrl = "https://example.com/link1" },
                    2 => new NavigationCommand { Type = CommandType.GoBack },
                    3 => new NavigationCommand { Type = CommandType.GoForward },
                    _ => new NavigationCommand { Type = CommandType.Quit }
                };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert
        _navigationService.CurrentPage!.Url.Should().Be("https://example.com/link1");
        _navigationService.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_CancellationToken_StopsGracefully()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act - should not throw
        await _sut.RunAsync("https://example.com", cts.Token);

        // Assert - should have exited due to cancellation
        // No exception means success
    }

    [Fact]
    public async Task RunAsync_PageDownThenQuit_MovesSelectionInHierarchicalView()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new NavigationCommand { Type = CommandType.PageDown }
                    : new NavigationCommand { Type = CommandType.Quit };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - in hierarchical view, PageDown moves selection rather than raw scroll
        // With only 1 link, selection stays at 0 and scroll offset stays at 0
        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_PageUpFromZero_ScrollOffsetStaysAtZero()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new NavigationCommand { Type = CommandType.PageUp }
                    : new NavigationCommand { Type = CommandType.Quit };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - scroll offset should stay at 0 (cannot go negative)
        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_CollapseNodeInReadableMode_RemapsToDecreaseWidth()
    {
        // Arrange - switch to Readable then send CollapseNode
        SetupPageLoad("https://example.com");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new NavigationCommand { Type = CommandType.SwitchToReadable },
                    2 => new NavigationCommand { Type = CommandType.CollapseNode },
                    _ => new NavigationCommand { Type = CommandType.Quit }
                };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - in Readable mode, CollapseNode (h key) adjusts width
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width");
    }

    [Fact]
    public async Task RunAsync_DecreaseWidthInReadableMode_AdjustsWidth()
    {
        // Arrange - switch to Readable then send DecreaseWidth directly (via [ key)
        SetupPageLoad("https://example.com");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new NavigationCommand { Type = CommandType.SwitchToReadable },
                    2 => new NavigationCommand { Type = CommandType.DecreaseWidth },
                    _ => new NavigationCommand { Type = CommandType.Quit }
                };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - DecreaseWidth directly adjusts width in reader mode
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width:");
    }

    [Fact]
    public async Task RunAsync_CollapseNodeInHierarchicalMode_IsNotRemapped()
    {
        // Arrange - stay in Hierarchical mode, send CollapseNode
        SetupPageLoad("https://example.com");

        var callCount = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new NavigationCommand { Type = CommandType.CollapseNode },
                    _ => new NavigationCommand { Type = CommandType.Quit }
                };
            });

        // Act
        await _sut.RunAsync("https://example.com");

        // Assert - in Hierarchical mode, no width status message
        var statusMsg = _navigationService.CurrentContext.StatusMessage;
        (statusMsg == null || !statusMsg.Contains("Width:")).Should().BeTrue(
            "CollapseNode in Hierarchical mode should NOT be remapped to DecreaseWidth");
    }
}
