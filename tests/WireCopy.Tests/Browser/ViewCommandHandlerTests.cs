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
/// Tests for ViewCommandHandler covering view switching and width adjustment.
/// </summary>
[Trait("Category", "Unit")]
// workspace-i8r7: HandleDumpHtml tests below mutate the process-global current directory
// (Directory.SetCurrentDirectory) and delete the temp dir afterwards. Serialize against the rest of the
// suite so that window never overlaps a concurrent test that resolves paths from the current directory
// (e.g. SchedulerShutdownContractTests building a host whose content root is the current directory).
[Collection(ProcessGlobalStateCollection.Name)]
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
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
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
                MaxContentWidth = _ctx!.ContentWidthOverride ?? 60
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

    [Theory]
    [InlineData(nameof(ViewCommandHandler.HandleSwitchView))]
    [InlineData(nameof(ViewCommandHandler.HandleSwitchToHierarchical))]
    [InlineData(nameof(ViewCommandHandler.HandleSwitchToReadable))]
    public async Task ViewModeKeys_InCollectionItems_AreNoOps(string methodName)
    {
        var collection = Domain.Entities.Collections.Collection.Create("Test Collection");
        collection.AddItem("https://example.com", "Test Item");
        _navigationService.EnterCollections();
        _navigationService.EnterCollection(collection);
        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionItems);

        var method = typeof(ViewCommandHandler).GetMethod(methodName)!;
        await (Task)method.Invoke(null, [_ctx, _options, CancellationToken.None])!;

        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionItems,
            $"{methodName} should be a no-op in CollectionItems view");
    }

    [Theory]
    [InlineData(nameof(ViewCommandHandler.HandleSwitchView))]
    [InlineData(nameof(ViewCommandHandler.HandleSwitchToHierarchical))]
    [InlineData(nameof(ViewCommandHandler.HandleSwitchToReadable))]
    public async Task ViewModeKeys_InCollectionList_AreNoOps(string methodName)
    {
        _navigationService.EnterCollections();
        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionList);

        var method = typeof(ViewCommandHandler).GetMethod(methodName)!;
        await (Task)method.Invoke(null, [_ctx, _options, CancellationToken.None])!;

        _navigationService.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionList,
            $"{methodName} should be a no-op in CollectionList view");
    }

    #endregion

    #region HandleIncreaseWidth

    [Fact]
    public async Task HandleIncreaseWidth_FromDefault_IncreasesBy10()
    {
        _ctx.ContentWidthOverride = null; // default = 60

        await ViewCommandHandler.HandleIncreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(70);
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
        _ctx.ContentWidthOverride = null; // default = 60

        await ViewCommandHandler.HandleDecreaseWidth(_ctx, _options, CancellationToken.None);

        _ctx.ContentWidthOverride.Should().Be(50);
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
        _lastRenderOptions!.MaxContentWidth.Should().Be(60);
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
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
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

        // GetCurrentRenderOptions returns width 60, cache was at 80 → width changed
        await ViewCommandHandler.HandleTerminalResized(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region Width Change Status Messages

    [Fact]
    public async Task HandleIncreaseWidth_SetsStatusMessageWithOldAndNewWidth()
    {
        _ctx.ContentWidthOverride = null; // default = 60

        await ViewCommandHandler.HandleIncreaseWidth(_ctx, _options, CancellationToken.None);

        // workspace-1m3h.5: announcement shows the transition, not just the result
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width 60 → 70");
    }

    [Fact]
    public async Task HandleDecreaseWidth_SetsStatusMessageWithOldAndNewWidth()
    {
        _ctx.ContentWidthOverride = null; // default = 60

        await ViewCommandHandler.HandleDecreaseWidth(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width 60 → 50");
    }

    [Fact]
    public async Task HandleIncreaseWidth_AtMax_AnnouncesMaximumInsteadOfTransition()
    {
        // workspace-1m3h.2: at the bound the width doesn't change — the message
        // must say so rather than imply an adjustment happened.
        _ctx.ContentWidthOverride = 120;

        await ViewCommandHandler.HandleIncreaseWidth(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width 120 (maximum)");
        _navigationService.CurrentContext.StatusMessage.Should().NotContain("→");
    }

    [Fact]
    public async Task HandleDecreaseWidth_AtMin_AnnouncesMinimumInsteadOfTransition()
    {
        _ctx.ContentWidthOverride = 40;

        await ViewCommandHandler.HandleDecreaseWidth(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width 40 (minimum)");
        _navigationService.CurrentContext.StatusMessage.Should().NotContain("→");
    }

    [Fact]
    public async Task HandleResetWidth_SetsStatusMessageWithDefault()
    {
        _ctx.ContentWidthOverride = 100;

        await ViewCommandHandler.HandleResetWidth(_ctx, _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Width 60");
        _navigationService.CurrentContext.StatusMessage.Should().Contain("default");
    }

    #endregion

    #region HandleDumpHtml

    [Fact]
    public async Task HandleDumpHtml_NoPage_SetsStatusMessage()
    {
        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var emptyNav = new NavigationService(navLogger);
        var ctx = new CommandContext
        {
            NavigationService = emptyNav,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = _lineCacheManager,
            ThemeProvider = Substitute.For<IThemeProvider>(),
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
            RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        await ViewCommandHandler.HandleDumpHtml(ctx, _options, CancellationToken.None);

        emptyNav.CurrentContext.StatusMessage.Should().Contain("No page loaded");
    }

    [Fact]
    public async Task HandleDumpHtml_WithPage_WritesFileAndSetsStatusMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"wirecopy_test_{Guid.NewGuid():N}");
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.SetCurrentDirectory(tempDir);

            await ViewCommandHandler.HandleDumpHtml(_ctx, _options, CancellationToken.None);

            var fixturesDir = Path.Combine(tempDir, "fixtures");
            Directory.Exists(fixturesDir).Should().BeTrue();
            var files = Directory.GetFiles(fixturesDir, "*.html");
            files.Should().HaveCount(1);

            var content = await File.ReadAllTextAsync(files[0]);
            content.Should().Contain("<html><body>Test</body></html>");

            var fileName = Path.GetFileName(files[0]);
            fileName.Should().StartWith("example_com_");

            _navigationService.CurrentContext.StatusMessage.Should().Contain("HTML dumped to fixtures/");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task HandleDumpHtml_SanitizesFileName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"wirecopy_test_{Guid.NewGuid():N}");
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.SetCurrentDirectory(tempDir);

            var page = Domain.Entities.Browser.Page.Create(
                "https://example.com/path/to/article?id=123&foo=bar",
                "<html>test</html>",
                new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
            _navigationService.NavigateTo(page);

            await ViewCommandHandler.HandleDumpHtml(_ctx, _options, CancellationToken.None);

            var files = Directory.GetFiles(Path.Combine(tempDir, "fixtures"), "*.html");
            files.Should().HaveCount(1);
            var fileName = Path.GetFileName(files[0]);
            fileName.Should().NotContain("?");
            fileName.Should().NotContain("&");
            fileName.Should().NotContain("/");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    #endregion

    #region HandleOpenInBrowser (workspace-kdda)

    [Fact]
    public async Task HandleOpenInBrowser_BlockedActionOnMatchingDomain_RoutesToHeadedChrome()
    {
        // Arrange: link list on www.example.com with selected link, preload
        // service reporting an active CAPTCHA on www.example.com.
        var page = CreateLinkListPage(
            pageUrl: "https://www.example.com/section",
            linkUrl: "https://www.example.com/article",
            linkType: LinkType.Content);
        _navigationService.NavigateTo(page);
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        var preload = Substitute.For<IPreloadService>();
        preload.GetProgress().Returns(new PreloadProgress
        {
            BlockedAction = new HumanActionRequired(HumanActionVariant.Captcha, "www.example.com"),
        });

        string? interactiveOpenedUrl = null;

        var ctx = WithOverrides(preload, (url, _, _) =>
        {
            interactiveOpenedUrl = url;
            return Task.CompletedTask;
        });

        await ViewCommandHandler.HandleOpenInBrowser(ctx, _options, CancellationToken.None);

        interactiveOpenedUrl.Should().Be("https://www.example.com/article",
            "the selected article URL on the captcha-blocked domain should be opened through the app's headed browser, not Process.Start");
    }

    [Fact]
    public async Task HandleOpenInBrowser_BlockedActionDifferentDomain_FallsThroughToSystemBrowser()
    {
        var page = CreateLinkListPage(
            pageUrl: "https://www.example.com/section",
            linkUrl: "https://otherdomain.example/article",
            linkType: LinkType.External);
        _navigationService.NavigateTo(page);
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        var preload = Substitute.For<IPreloadService>();
        preload.GetProgress().Returns(new PreloadProgress
        {
            // CAPTCHA is on a different domain than the link the user is
            // about to open — don't hijack Shift+O for an unrelated URL.
            BlockedAction = new HumanActionRequired(HumanActionVariant.Captcha, "www.example.com"),
        });

        var interactiveCalls = 0;
        var ctx = WithOverrides(preload, (_, _, _) =>
        {
            interactiveCalls++;
            return Task.CompletedTask;
        });

        await ViewCommandHandler.HandleOpenInBrowser(ctx, _options, CancellationToken.None);

        interactiveCalls.Should().Be(0,
            "the blocked-action domain doesn't match the URL the user is opening, so the headed-Chrome route must not be used");
    }

    [Fact]
    public async Task HandleOpenInBrowser_NoBlockedAction_FallsThroughToSystemBrowser()
    {
        var page = CreateLinkListPage(
            pageUrl: "https://www.example.com/section",
            linkUrl: "https://www.example.com/article",
            linkType: LinkType.Content);
        _navigationService.NavigateTo(page);
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        var preload = Substitute.For<IPreloadService>();
        preload.GetProgress().Returns(new PreloadProgress
        {
            BlockedAction = null,
        });

        var interactiveCalls = 0;
        var ctx = WithOverrides(preload, (_, _, _) =>
        {
            interactiveCalls++;
            return Task.CompletedTask;
        });

        await ViewCommandHandler.HandleOpenInBrowser(ctx, _options, CancellationToken.None);

        interactiveCalls.Should().Be(0,
            "without a blocked action the old system-browser path should run, not the headed-Chrome route");
    }

    private static Domain.Entities.Browser.Page CreateLinkListPage(string pageUrl, string linkUrl, LinkType linkType)
    {
        var page = Domain.Entities.Browser.Page.Create(
            pageUrl,
            "<html/>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Section" });
        var tree = Domain.Entities.Browser.NavigationTree.Build(
            new List<Domain.ValueObjects.Browser.LinkInfo>
            {
                new()
                {
                    Url = linkUrl,
                    DisplayText = "Test article",
                    Type = linkType,
                    ImportanceScore = 50,
                },
            });
        page.SetLinkTree(tree);
        return page;
    }

    private CommandContext WithOverrides(
        IPreloadService preload,
        Func<string, RenderOptions, CancellationToken, Task> openInteractive)
    {
        return new CommandContext
        {
            NavigationService = _ctx.NavigationService,
            Renderer = _ctx.Renderer,
            InputHandler = _ctx.InputHandler,
            ScopeFactory = _ctx.ScopeFactory,
            Logger = _ctx.Logger,
            PageCache = _ctx.PageCache,
            LineCacheManager = _ctx.LineCacheManager,
            ThemeProvider = _ctx.ThemeProvider,
            PreloadService = preload,
            LayoutVariantProvider = _ctx.LayoutVariantProvider,
            NavigateToAsync = _ctx.NavigateToAsync,
            ForceRefreshAsync = _ctx.ForceRefreshAsync,
            InteractiveRefreshAsync = _ctx.InteractiveRefreshAsync,
            OpenInteractiveBrowserAsync = openInteractive,
            SetOverlayPainter = _ => { },
            RenderCurrentPageAsync = _ctx.RenderCurrentPageAsync,
            RefreshCollectionsAsync = _ctx.RefreshCollectionsAsync,
            RefreshBookmarksAsync = _ctx.RefreshBookmarksAsync,
            GetCurrentRenderOptions = _ctx.GetCurrentRenderOptions,
            CreateCollectionService = _ctx.CreateCollectionService,
            GetReaderViewportHeight = _ctx.GetReaderViewportHeight,
            GetHierarchicalViewportHeight = _ctx.GetHierarchicalViewportHeight,
            AdjustScrollForSelection = _ctx.AdjustScrollForSelection,
            ScrollToSearchMatch = _ctx.ScrollToSearchMatch,
        };
    }

    #endregion
}
