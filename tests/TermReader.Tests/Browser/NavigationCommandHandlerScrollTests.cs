// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for NavigationCommandHandler scroll behavior in Readable view mode.
/// Verifies MoveDown/MoveUp/PageDown/PageUp/GoToBottom advance scroll correctly
/// when CachedLines is populated by EnsureLineCache.
/// </summary>
public class NavigationCommandHandlerScrollTests
{
    private readonly NavigationService _navigationService;
    private readonly LineCacheManager _lineCacheManager;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly List<string> _testLines;
    private bool _renderCalled;

    private const int ViewportHeight = 20;

    public NavigationCommandHandlerScrollTests()
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
        _navigationService.SetViewMode(ViewMode.Readable);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 76
        };

        // 50 lines of content — more than viewport height
        _testLines = Enumerable.Range(1, 50).Select(i => $"  Line {i}").ToList();

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _lineCacheManager = new LineCacheManager(_navigationService, themeProvider);
        _lineCacheManager.SetCacheForTesting(_testLines, 76);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = _lineCacheManager,
            ThemeProvider = themeProvider,
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
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
            GetReaderViewportHeight = _ => ViewportHeight,
            GetHierarchicalViewportHeight = _ => ViewportHeight,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    #region HandleMoveDown - Readable scroll

    [Fact]
    public async Task HandleMoveDown_Readable_IncrementsScrollByOne()
    {
        _navigationService.SetScrollOffset(0);

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(1);
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMoveDown_Readable_IncrementsFromExistingOffset()
    {
        _navigationService.SetScrollOffset(10);

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(11);
    }

    [Fact]
    public async Task HandleMoveDown_Readable_ClampsToMaxOffset()
    {
        // maxOffset = max(0, 50 - 20) = 30
        _navigationService.SetScrollOffset(30);

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(30);
    }

    [Fact]
    public async Task HandleMoveDown_Readable_PopulatesCachedLinesViaEnsureLineCache()
    {
        _lineCacheManager.InvalidateLineCache();

        await NavigationCommandHandler.HandleMoveDown(_ctx, _options, CancellationToken.None);

        // EnsureLineCache should rebuild from the page's ReadableContent
        _lineCacheManager.CachedLines.Should().NotBeNull();
    }

    #endregion

    #region HandleMoveUp - Readable scroll

    [Fact]
    public async Task HandleMoveUp_Readable_DecrementsScrollByOne()
    {
        _navigationService.SetScrollOffset(10);

        await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(9);
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMoveUp_Readable_ClampsToZero()
    {
        _navigationService.SetScrollOffset(0);

        await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public async Task HandleMoveUp_Readable_DecrementsFromOne()
    {
        _navigationService.SetScrollOffset(1);

        await NavigationCommandHandler.HandleMoveUp(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    #endregion

    #region HandlePageDown - Readable scroll

    [Fact]
    public async Task HandlePageDown_Readable_ScrollsByHalfPage()
    {
        _navigationService.SetScrollOffset(0);

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        // halfPage = max(1, 20/2) = 10
        _navigationService.CurrentContext.ScrollOffset.Should().Be(10);
    }

    [Fact]
    public async Task HandlePageDown_Readable_ClampsToMaxOffset()
    {
        // maxOffset = 30, already at 25, halfPage = 10 → would be 35, clamped to 30
        _navigationService.SetScrollOffset(25);

        await NavigationCommandHandler.HandlePageDown(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(30);
    }

    #endregion

    #region HandlePageUp - Readable scroll

    [Fact]
    public async Task HandlePageUp_Readable_ScrollsBackByHalfPage()
    {
        _navigationService.SetScrollOffset(20);

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        // halfPage = 10, 20 - 10 = 10
        _navigationService.CurrentContext.ScrollOffset.Should().Be(10);
    }

    [Fact]
    public async Task HandlePageUp_Readable_ClampsToZero()
    {
        _navigationService.SetScrollOffset(5);

        await NavigationCommandHandler.HandlePageUp(_ctx, _options, CancellationToken.None);

        // halfPage = 10, 5 - 10 = -5, clamped to 0
        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    #endregion

    #region HandleGoToBottom - Readable scroll

    [Fact]
    public async Task HandleGoToBottom_Readable_ScrollsToEnd()
    {
        _navigationService.SetScrollOffset(0);

        await NavigationCommandHandler.HandleGoToBottom(_ctx, _options, CancellationToken.None);

        // maxOffset = max(0, 50 - 20) = 30
        _navigationService.CurrentContext.ScrollOffset.Should().Be(30);
    }

    [Fact]
    public async Task HandleGoToBottom_Readable_AlreadyAtEnd()
    {
        _navigationService.SetScrollOffset(30);

        await NavigationCommandHandler.HandleGoToBottom(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(30);
    }

    #endregion

    #region HandleGoToTop - Readable scroll

    [Fact]
    public async Task HandleGoToTop_Readable_ScrollsToStart()
    {
        _navigationService.SetScrollOffset(25);

        await NavigationCommandHandler.HandleGoToTop(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ScrollOffset.Should().Be(0);
    }

    #endregion
}
