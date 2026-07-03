// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for SearchCommandHandler covering search, search navigation, and command-line input.
/// </summary>
[Trait("Category", "Unit")]
public class SearchCommandHandlerTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly IInputHandler _inputHandler;
    private bool _renderCalled;
    private string? _navigatedUrl;
    private int? _lastScrollToSearchMatchIndex;

    public SearchCommandHandlerTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        _navigationService.NavigateTo(page);

        _inputHandler = Substitute.For<IInputHandler>();

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 80
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = _inputHandler,
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(_navigationService, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (url, _, _) =>
            {
                _navigatedUrl = url;
                return Task.CompletedTask;
            },
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
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (index, _) => { _lastScrollToSearchMatchIndex = index; },
        };
    }

    #region HandleSearch

    [Fact]
    public async Task HandleSearch_WithQuery_SetsSearchQueryOnNavigationService()
    {
        _inputHandler.PromptForInputAsync("/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("test query"));

        await SearchCommandHandler.HandleSearch(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.SearchQuery.Should().Be("test query");
    }

    [Fact]
    public async Task HandleSearch_WithEmptyQuery_DoesNotSetSearchQuery()
    {
        _inputHandler.PromptForInputAsync("/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(""));

        await SearchCommandHandler.HandleSearch(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.SearchQuery.Should().BeNull();
    }

    [Fact]
    public async Task HandleSearch_AlwaysRendersAfterPrompt()
    {
        _inputHandler.PromptForInputAsync("/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        await SearchCommandHandler.HandleSearch(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region HandleSearchNext / HandleSearchPrevious

    [Fact]
    public async Task HandleSearchNext_WithNoQuery_DoesNotChangeMatchIndex()
    {
        // No search query set
        await SearchCommandHandler.HandleSearchNext(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.SearchMatchIndex.Should().Be(0);
    }

    [Fact]
    public async Task HandleSearchNext_WithQuery_CallsScrollToSearchMatchWithIncrementedIndex()
    {
        _navigationService.SetSearchQuery("test");
        _navigationService.SetSearchMatchIndex(0);

        await SearchCommandHandler.HandleSearchNext(_ctx, _options, CancellationToken.None);

        _lastScrollToSearchMatchIndex.Should().Be(1);
    }

    [Fact]
    public async Task HandleSearchPrevious_WithQuery_CallsScrollToSearchMatchWithDecrementedIndex()
    {
        _navigationService.SetSearchQuery("test");
        _navigationService.SetSearchMatchIndex(2);

        await SearchCommandHandler.HandleSearchPrevious(_ctx, _options, CancellationToken.None);

        _lastScrollToSearchMatchIndex.Should().Be(1);
    }

    #endregion

    #region Search blocked in collection views

    [Fact]
    public async Task HandleSearch_InCollectionList_SetsStatusMessageAndDoesNotPrompt()
    {
        _navigationService.EnterCollections();

        await SearchCommandHandler.HandleSearch(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Be("Search not available in collections");
        _navigationService.CurrentContext.SearchQuery.Should().BeNull();
        _renderCalled.Should().BeTrue();
        await _inputHandler.DidNotReceive().PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSearch_InCollectionItems_SetsStatusMessageAndDoesNotPrompt()
    {
        _navigationService.EnterCollections();
        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Test Collection");
        _navigationService.EnterCollection(collection);

        await SearchCommandHandler.HandleSearch(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Be("Search not available in collections");
        _navigationService.CurrentContext.SearchQuery.Should().BeNull();
        _renderCalled.Should().BeTrue();
        await _inputHandler.DidNotReceive().PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleSearchNext_InCollectionList_DoesNothing()
    {
        _navigationService.SetSearchQuery("test");
        _navigationService.EnterCollections();

        await SearchCommandHandler.HandleSearchNext(_ctx, _options, CancellationToken.None);

        _lastScrollToSearchMatchIndex.Should().BeNull();
        _renderCalled.Should().BeFalse();
    }

    [Fact]
    public async Task HandleSearchPrevious_InCollectionItems_DoesNothing()
    {
        _navigationService.SetSearchQuery("test");
        _navigationService.EnterCollections();
        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Test");
        _navigationService.EnterCollection(collection);

        await SearchCommandHandler.HandleSearchPrevious(_ctx, _options, CancellationToken.None);

        _lastScrollToSearchMatchIndex.Should().BeNull();
        _renderCalled.Should().BeFalse();
    }

    #endregion

    #region HandleCommandLineInput

    [Fact]
    public async Task HandleCommandLineInput_OpenCommand_NavigatesToUrl()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "open https://google.com", _options, CancellationToken.None);

        _navigatedUrl.Should().Be("https://google.com");
    }

    [Fact]
    public async Task HandleCommandLineInput_LayoutCommand_RoutesToChooser_NotUrlNavigation()
    {
        // workspace-5oe9.10: ':layout' is consolidated onto the strategy chooser
        // (StrategyChooserHandler) — it must be handled, not fall through to URL
        // navigation, and must not throw with the chooser's services absent.
        var handled = await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "layout", _options, CancellationToken.None);

        handled.Should().BeTrue();
        _navigatedUrl.Should().BeNull("':layout' opens the chooser, it is not a URL");
    }

    [Fact]
    public async Task HandleCommandLineInput_GoCommand_NavigatesToUrl()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "go example.org", _options, CancellationToken.None);

        _navigatedUrl.Should().Be("https://example.org");
    }

    [Fact]
    public async Task HandleCommandLineInput_BareDomain_NavigatesToUrl()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "wikipedia.org", _options, CancellationToken.None);

        _navigatedUrl.Should().Be("https://wikipedia.org");
    }

    [Fact]
    public async Task HandleCommandLineInput_UrlWithProtocol_PreservesProtocol()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "http://insecure.example.com", _options, CancellationToken.None);

        _navigatedUrl.Should().Be("http://insecure.example.com");
    }

    #endregion

    #region Unknown commands (workspace-syj1.1 / .5)

    [Fact]
    public async Task HandleCommandLineInput_MistypedCommand_ShowsUnknownCommandInsteadOfNavigating()
    {
        var handled = await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "hlep", _options, CancellationToken.None);

        handled.Should().BeTrue();
        _navigatedUrl.Should().BeNull("a bare word without a dot is a typo'd command, not a URL");
        _navigationService.CurrentContext.StatusMessage.Should().Be("Unknown command: hlep. Type :help for commands");
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleCommandLineInput_MistypedCommandWithArgument_ShowsUnknownCommandInsteadOfNavigating()
    {
        // workspace-syj1.5: ':opne google.com' must not become https://opne%20google.com
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "opne google.com", _options, CancellationToken.None);

        _navigatedUrl.Should().BeNull("input containing a space is never a URL");
        _navigationService.CurrentContext.StatusMessage.Should().Be("Unknown command: opne. Type :help for commands");
    }

    [Fact]
    public async Task HandleCommandLineInput_HostWithPort_StillNavigates()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "localhost:8080", _options, CancellationToken.None);

        _navigatedUrl.Should().Be("https://localhost:8080");
    }

    [Theory]
    [InlineData("wikipedia.org", true)]
    [InlineData("sub.domain.co.uk", true)]
    [InlineData("example.com/some/path?q=1", true)]
    [InlineData("http://anything", true)]
    [InlineData("https://anything", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("devbox:3000", true)]
    [InlineData("hlep", false)]
    [InlineData("opne google.com", false)]
    [InlineData("two words", false)]
    [InlineData("q!", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(":8080", false)]
    [InlineData("host:notaport", false)]
    [InlineData("host:", false)]
    public void LooksLikeUrl_ClassifiesInput(string input, bool expected)
    {
        SearchCommandHandler.LooksLikeUrl(input).Should().Be(expected, $"input was '{input}'");
    }

    #endregion

    #region Missing-argument usage messages (workspace-syj1.2)

    [Fact]
    public async Task HandleCommandLineInput_OpenWithoutArgument_ShowsUsage()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "open", _options, CancellationToken.None);

        _navigatedUrl.Should().BeNull();
        _navigationService.CurrentContext.StatusMessage.Should().Be("Usage: :open <url>");
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleCommandLineInput_NewWithoutArgument_ShowsUsage()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "new", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Be("Usage: :new <name>");
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleCommandLineInput_RenameWithoutArgument_ShowsUsage()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "rename", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Be("Usage: :rename <name>");
        _renderCalled.Should().BeTrue();
    }

    #endregion
}
