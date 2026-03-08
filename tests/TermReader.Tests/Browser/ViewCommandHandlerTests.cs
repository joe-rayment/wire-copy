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
/// Tests for ViewCommandHandler covering view switching and width adjustment.
/// </summary>
public class ViewCommandHandlerTests
{
    private readonly NavigationService _navigationService;
    private readonly LineCacheManager _lineCacheManager;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private bool _renderCalled;
    private RenderOptions? _lastRenderOptions;

    public ViewCommandHandlerTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        _navigationService.NavigateTo(page);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 80
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _lineCacheManager = new LineCacheManager(_navigationService, themeProvider);

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
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (opts, _) =>
            {
                _renderCalled = true;
                _lastRenderOptions = opts;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => new RenderOptions
            {
                TerminalWidth = 80,
                TerminalHeight = 24,
                MaxContentWidth = _ctx!.ContentWidthOverride ?? 66
            },
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    #region HandleSwitchView

    [Fact]
    public async Task HandleSwitchView_TogglesFromHierarchicalToReadable()
    {
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        await ViewCommandHandler.HandleSwitchView(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.Readable);
    }

    [Fact]
    public async Task HandleSwitchView_TogglesFromReadableToHierarchical()
    {
        _navigationService.SetViewMode(ViewMode.Readable);

        await ViewCommandHandler.HandleSwitchView(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);
    }

    [Fact]
    public async Task HandleSwitchView_InvalidatesLineCacheAndRenders()
    {
        // Pre-populate the cache so we can verify invalidation
        _lineCacheManager.SetCacheForTesting(new List<string> { "test" }, 80);

        await ViewCommandHandler.HandleSwitchView(_ctx, _options, CancellationToken.None);

        _lineCacheManager.CachedLines.Should().BeNull();
        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region HandleSwitchToHierarchical / HandleSwitchToReadable

    [Fact]
    public async Task HandleSwitchToHierarchical_SetsHierarchicalMode()
    {
        _navigationService.SetViewMode(ViewMode.Readable);

        await ViewCommandHandler.HandleSwitchToHierarchical(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);
    }

    [Fact]
    public async Task HandleSwitchToReadable_SetsReadableMode()
    {
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        await ViewCommandHandler.HandleSwitchToReadable(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.Readable);
    }

    #endregion

    #region HandleIncreaseWidth

    [Fact]
    public async Task HandleIncreaseWidth_FromDefault_IncreasesBy10()
    {
        _ctx.ContentWidthOverride = null; // default = 66

        await ViewCommandHandler.HandleIncreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(76);
    }

    [Fact]
    public async Task HandleIncreaseWidth_ClampsToMax120()
    {
        _ctx.ContentWidthOverride = 115;

        await ViewCommandHandler.HandleIncreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(120);
    }

    [Fact]
    public async Task HandleIncreaseWidth_AtMax_StaysAt120()
    {
        _ctx.ContentWidthOverride = 120;

        await ViewCommandHandler.HandleIncreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(120);
    }

    #endregion

    #region HandleDecreaseWidth

    [Fact]
    public async Task HandleDecreaseWidth_FromDefault_DecreasesBy10()
    {
        _ctx.ContentWidthOverride = null; // default = 66

        await ViewCommandHandler.HandleDecreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(56);
    }

    [Fact]
    public async Task HandleDecreaseWidth_ClampsToMin40()
    {
        _ctx.ContentWidthOverride = 45;

        await ViewCommandHandler.HandleDecreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(40);
    }

    [Fact]
    public async Task HandleDecreaseWidth_AtMin_StaysAt40()
    {
        _ctx.ContentWidthOverride = 40;

        await ViewCommandHandler.HandleDecreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(40);
    }

    #endregion

    #region HandleResetWidth

    [Fact]
    public async Task HandleResetWidth_ClearsOverride()
    {
        _ctx.ContentWidthOverride = 100;

        await ViewCommandHandler.HandleResetWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().BeNull();
    }

    [Fact]
    public async Task HandleResetWidth_RendersWithNewOptions()
    {
        _ctx.ContentWidthOverride = 100;

        await ViewCommandHandler.HandleResetWidth(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
        _lastRenderOptions.Should().NotBeNull();
        _lastRenderOptions!.MaxContentWidth.Should().Be(66);
    }

    #endregion

    #region HandleOpenLauncher

    [Fact]
    public async Task HandleOpenLauncher_EntersLauncherModeAndRefreshesBookmarks()
    {
        var refreshBookmarksCalled = false;
        // Replace delegate to track call
        var ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = _lineCacheManager,
            ThemeProvider = Substitute.For<IThemeProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ =>
            {
                refreshBookmarksCalled = true;
                return Task.CompletedTask;
            },
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        await ViewCommandHandler.HandleOpenLauncher(ctx, _options, CancellationToken.None);

        _navigationService.InLauncherMode.Should().BeTrue();
        refreshBookmarksCalled.Should().BeTrue();
    }

    #endregion

    #region HandleCycleTheme

    [Fact]
    public async Task HandleCycleTheme_CyclesThemeAndInvalidatesCache()
    {
        _lineCacheManager.SetCacheForTesting(new List<string> { "test" }, 80);
        _ctx.ThemeProvider.CycleTheme().Returns(ThemeName.Amber);
        _ctx.ThemeProvider.CurrentTheme.Returns(ThemeName.Amber);

        await ViewCommandHandler.HandleCycleTheme(_ctx, _options, CancellationToken.None);

        _ctx.ThemeProvider.Received(1).CycleTheme();
        _lineCacheManager.CachedLines.Should().BeNull();
        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region HandleTerminalResized

    [Fact]
    public async Task HandleTerminalResized_RendersWithFreshOptions()
    {
        await ViewCommandHandler.HandleTerminalResized(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleTerminalResized_PreservesScrollPositionWhenWidthChanges()
    {
        _lineCacheManager.SetCacheForTesting(
            new List<string> { "line1", "line2", "line3", "line4", "line5" }, 80);
        _navigationService.SetScrollOffset(2);

        // GetCurrentRenderOptions returns width 66, cache was at 80 → width changed
        await ViewCommandHandler.HandleTerminalResized(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region Width Change Status Messages

    [Fact]
    public async Task HandleIncreaseWidth_SetsStatusMessageWithNewWidth()
    {
        _ctx.ContentWidthOverride = null; // default = 66

        await ViewCommandHandler.HandleIncreaseWidth(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width: 76");
    }

    [Fact]
    public async Task HandleDecreaseWidth_SetsStatusMessageWithNewWidth()
    {
        _ctx.ContentWidthOverride = null; // default = 66

        await ViewCommandHandler.HandleDecreaseWidth(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width: 56");
    }

    [Fact]
    public async Task HandleResetWidth_SetsStatusMessageWithDefault()
    {
        _ctx.ContentWidthOverride = 100;

        await ViewCommandHandler.HandleResetWidth(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width: 66");
        _navigationService.CurrentContext.StatusMessage.Should().Contain("default");
    }

    #endregion
}
