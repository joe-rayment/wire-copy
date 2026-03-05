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
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private bool _lineCacheInvalidated;
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

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
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
            InvalidateLineCache = () => _lineCacheInvalidated = true,
            EnsureLineCache = _ => { },
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
            PreserveScrollPositionAfterRewrap = _ => { }
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
        await ViewCommandHandler.HandleSwitchView(_ctx, _options, CancellationToken.None);

        _lineCacheInvalidated.Should().BeTrue();
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
}
