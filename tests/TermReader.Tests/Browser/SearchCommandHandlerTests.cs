// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for SearchCommandHandler covering search, search navigation, and command-line input.
/// </summary>
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

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = _inputHandler,
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            NavigateToAsync = (url, _, _) =>
            {
                _navigatedUrl = url;
                return Task.CompletedTask;
            },
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) =>
            {
                _renderCalled = true;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            InvalidateLineCache = () => { },
            EnsureLineCache = _ => { },
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (index, _) => { _lastScrollToSearchMatchIndex = index; },
            PreserveScrollPositionAfterRewrap = _ => { }
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

    #region HandleCommandLineInput

    [Fact]
    public async Task HandleCommandLineInput_OpenCommand_NavigatesToUrl()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "open https://google.com", _options, CancellationToken.None);

        _navigatedUrl.Should().Be("https://google.com");
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
}
