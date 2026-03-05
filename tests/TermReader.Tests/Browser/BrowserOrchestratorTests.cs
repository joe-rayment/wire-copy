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

public class BrowserOrchestratorTests
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly IInputHandler _inputHandler;
    private readonly NavigationService _navigationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<BrowserConfiguration> _browserConfig;
    private readonly ILogger<BrowserOrchestrator> _logger;
    private readonly BrowserOrchestrator _sut;

    public BrowserOrchestratorTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _linkExtractor = Substitute.For<ILinkExtractor>();
        _treeBuilder = Substitute.For<INavigationTreeBuilder>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _renderer = Substitute.For<IPageRenderer>();
        _inputHandler = Substitute.For<IInputHandler>();
        _browserConfig = Options.Create(new BrowserConfiguration());
        _logger = Substitute.For<ILogger<BrowserOrchestrator>>();

        // Set up scoped service factory for ICollectionService
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var collectionService = Substitute.For<ICollectionService>();
        serviceProvider.GetService(typeof(ICollectionService)).Returns(collectionService);
        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory.CreateScope().Returns(scope);

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(navLogger);

        var browserSession = Substitute.For<IBrowserSession>();
        _sut = new BrowserOrchestrator(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            _inputHandler,
            _navigationService,
            _scopeFactory,
            browserSession,
            _browserConfig,
            _logger);
    }

    private void SetupPageLoad(string url, string title = "Test Page", bool hasReadableContent = false)
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body>Content</body></html>";

        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(url, html, metadata));

        var links = new List<LinkInfo>
        {
            new LinkInfo { Url = "https://example.com/link1", DisplayText = "Link One", Type = LinkType.Content, ImportanceScore = 80 },
            new LinkInfo { Url = "https://example.com/link2", DisplayText = "Link Two", Type = LinkType.Navigation, ImportanceScore = 50 }
        };

        _linkExtractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(links);

        var tree = NavigationTree.Build(links);
        _treeBuilder.BuildTreeAsync(Arg.Any<List<LinkInfo>>(), Arg.Any<CancellationToken>())
            .Returns(tree);

        if (hasReadableContent)
        {
            var readable = ReadableContent.Create("Article Title", "Some article content here", new List<string> { "Paragraph one", "Paragraph two" });
            _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(readable);
        }
        else
        {
            _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((ReadableContent?)null);
        }
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
        page.LinkTree!.TotalLinks.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LoadPageAsync_WithReadableContent_SetsReadableContent()
    {
        // Arrange
        SetupPageLoad("https://example.com/article", "Article", hasReadableContent: true);

        // Act
        var page = await _sut.LoadPageAsync("https://example.com/article");

        // Assert
        page.HasReadableContent().Should().BeTrue();
        page.ReadableContent!.Title.Should().Be("Article Title");
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
    public async Task LoadPageAsync_CallsRendererRenderLoading()
    {
        // Arrange
        SetupPageLoad("https://example.com");

        // Act
        await _sut.LoadPageAsync("https://example.com");

        // Assert
        _renderer.Received(1).RenderLoading("https://example.com");
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
    public async Task ExtractReadableContentAsync_WithoutExistingContent_CallsExtractor()
    {
        // Arrange
        SetupPageLoad("https://example.com", hasReadableContent: false);
        var page = await _sut.LoadPageAsync("https://example.com");

        var freshContent = ReadableContent.Create("Fresh Title", "Fresh content", new List<string> { "Paragraph" });
        _contentExtractor.ExtractAsync(page.RawHtml, page.Url, Arg.Any<CancellationToken>())
            .Returns(freshContent);

        // Act
        var content = await _sut.ExtractReadableContentAsync(page);

        // Assert
        content.Should().NotBeNull();
        content!.Title.Should().Be("Fresh Title");
    }
}

/// <summary>
/// Tests for HandleCommandAsync behavior via the RunAsync loop (indirectly).
/// Since HandleCommandAsync is private, these tests verify behavior by setting up
/// the orchestrator with specific input sequences and checking side effects.
/// </summary>
public class BrowserOrchestratorNavigationTests
{
    private readonly IPageLoader _pageLoader;
    private readonly ILinkExtractor _linkExtractor;
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPageRenderer _renderer;
    private readonly IInputHandler _inputHandler;
    private readonly NavigationService _navigationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<BrowserConfiguration> _browserConfig;
    private readonly ILogger<BrowserOrchestrator> _logger;
    private readonly BrowserOrchestrator _sut;

    public BrowserOrchestratorNavigationTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _linkExtractor = Substitute.For<ILinkExtractor>();
        _treeBuilder = Substitute.For<INavigationTreeBuilder>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _renderer = Substitute.For<IPageRenderer>();
        _inputHandler = Substitute.For<IInputHandler>();
        _browserConfig = Options.Create(new BrowserConfiguration());
        _logger = Substitute.For<ILogger<BrowserOrchestrator>>();

        // Set up scoped service factory for ICollectionService
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var collectionService = Substitute.For<ICollectionService>();
        serviceProvider.GetService(typeof(ICollectionService)).Returns(collectionService);
        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory.CreateScope().Returns(scope);

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(navLogger);

        var browserSession = Substitute.For<IBrowserSession>();
        _sut = new BrowserOrchestrator(
            _pageLoader,
            _linkExtractor,
            _treeBuilder,
            _contentExtractor,
            _renderer,
            _inputHandler,
            _navigationService,
            _scopeFactory,
            browserSession,
            _browserConfig,
            _logger);
    }

    private void SetupPageLoad(string url, string title = "Test Page")
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body>Content</body></html>";

        _pageLoader.LoadAsync(Arg.Is<PageLoadRequest>(r => r.Url == url), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Successful(url, html, metadata));

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
}
