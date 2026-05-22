// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-c8v3 — keybind + state-flag wiring tests for the prefetch
/// detail overlay toggle. Covers (1) backslash maps to TogglePreloadDetail,
/// (2) Esc still maps to GoBack at the mapper level, (3) Ctrl+P keeps
/// CycleTheme, (4) ShowPreloadDetail defaults to false, (5) the toggle
/// command round-trips through LauncherCommandHandler and flips the flag,
/// (6) Esc/GoBack dismisses the overlay first when visible (no fall-through
/// to the legacy bookmark refresh), (7) when overlay is hidden the legacy
/// GoBack path still runs.
/// </summary>
[Trait("Category", "Unit")]
public class PreloadDetailToggleTests
{
    // ---- Mapping tests ----

    private static TerminalInputHandler MakeHandler()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var resizeDetector = Substitute.For<IResizeDetector>();
        var navigationService = Substitute.For<INavigationService>();
        var logger = Substitute.For<ILogger<TerminalInputHandler>>();
        return new TerminalInputHandler(themeProvider, resizeDetector, navigationService, logger);
    }

    private static NavigationCommand MapKeyInfo(TerminalInputHandler handler, ConsoleKeyInfo keyInfo)
    {
        var method = typeof(TerminalInputHandler)
            .GetMethod("MapKeyInfoToCommand", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "MapKeyInfoToCommand is required by these tests — if it was renamed, update the binding.");
        return (NavigationCommand)method.Invoke(handler, new object[] { keyInfo })!;
    }

    [Fact]
    public void Backslash_MapsToTogglePreloadDetail()
    {
        var handler = MakeHandler();
        var keyInfo = new ConsoleKeyInfo('\\', ConsoleKey.Oem5, false, false, false);

        var cmd = MapKeyInfo(handler, keyInfo);

        cmd.Type.Should().Be(CommandType.TogglePreloadDetail,
            because: "the prefetch detail panel is bound to backslash so it works from any non-modal view");
    }

    [Fact]
    public void Escape_DoesNotMapToTogglePreloadDetail()
    {
        var handler = MakeHandler();
        var cmd = handler.MapKeyToCommand(ConsoleKey.Escape, 0);

        cmd.Type.Should().Be(CommandType.GoBack,
            because: "the dismiss interception lives in the command handler, not the key mapper");
    }

    [Fact]
    public void CtrlP_StillMapsToCycleTheme_NotTogglePreloadDetail()
    {
        var handler = MakeHandler();
        var cmd = handler.MapKeyToCommand(ConsoleKey.P, ConsoleModifiers.Control);

        cmd.Type.Should().Be(CommandType.CycleTheme,
            because: "regression guard — workspace-j8cp listed Ctrl+P as a candidate; we picked \\ instead");
    }

    [Fact]
    public void RenderOptions_ShowPreloadDetail_DefaultsToFalse()
    {
        var options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 30,
            MaxContentWidth = 100,
        };

        options.ShowPreloadDetail.Should().BeFalse(
            because: "the panel must be hidden by default — no visible change at startup");
    }

    // ---- Handler integration tests ----

    private static CommandContext MakeLauncherContext(
        Func<CancellationToken, Task>? refreshBookmarks = null)
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        navigationService.NavigateTo(page);
        navigationService.EnterLauncher();

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var lineCacheManager = new LineCacheManager(navigationService, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 30,
            MaxContentWidth = 96,
        };

        CommandContext? ctx = null;
        ctx = new CommandContext
        {
            NavigationService = navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = refreshBookmarks ?? (_ => Task.CompletedTask),
            GetCurrentRenderOptions = () => options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 24,
            GetHierarchicalViewportHeight = _ => 24,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        return ctx;
    }

    [Fact]
    public async Task LauncherCommandHandler_TogglePreloadDetail_FlipsCommandContextFlag()
    {
        var ctx = MakeLauncherContext();
        var options = ctx.GetCurrentRenderOptions();
        ctx.IsPreloadDetailVisible.Should().BeFalse("starts hidden by default");

        await LauncherCommandHandler.Handle(
            ctx,
            new NavigationCommand { Type = CommandType.TogglePreloadDetail },
            options,
            CancellationToken.None);
        ctx.IsPreloadDetailVisible.Should().BeTrue("first \\ press toggles the panel ON");

        await LauncherCommandHandler.Handle(
            ctx,
            new NavigationCommand { Type = CommandType.TogglePreloadDetail },
            options,
            CancellationToken.None);
        ctx.IsPreloadDetailVisible.Should().BeFalse("second \\ press toggles the panel OFF");
    }

    [Fact]
    public async Task LauncherCommandHandler_GoBack_WhenOverlayVisible_DismissesAndSkipsLegacyBookmarkRefresh()
    {
        var refreshBookmarksCalled = false;
        var ctx = MakeLauncherContext(_ =>
        {
            refreshBookmarksCalled = true;
            return Task.CompletedTask;
        });
        ctx.IsPreloadDetailVisible = true;

        await LauncherCommandHandler.Handle(
            ctx,
            new NavigationCommand { Type = CommandType.GoBack },
            ctx.GetCurrentRenderOptions(),
            CancellationToken.None);

        ctx.IsPreloadDetailVisible.Should().BeFalse(
            "GoBack while overlay is visible must clear the flag");
        refreshBookmarksCalled.Should().BeFalse(
            "dismiss path must NOT fall through to the legacy bookmark-refresh");
    }

    [Fact]
    public async Task LauncherCommandHandler_GoBack_WhenOverlayHidden_StillCallsLegacyBookmarkRefresh()
    {
        var refreshBookmarksCalled = false;
        var ctx = MakeLauncherContext(_ =>
        {
            refreshBookmarksCalled = true;
            return Task.CompletedTask;
        });

        ctx.IsPreloadDetailVisible.Should().BeFalse();

        await LauncherCommandHandler.Handle(
            ctx,
            new NavigationCommand { Type = CommandType.GoBack },
            ctx.GetCurrentRenderOptions(),
            CancellationToken.None);

        refreshBookmarksCalled.Should().BeTrue(
            "with overlay hidden, GoBack must keep the legacy launcher behaviour");
    }
}
