// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Shared helper to construct BrowserOrchestrator with sensible defaults,
/// eliminating 3x duplicated constructor boilerplate across test classes.
/// </summary>
internal static class BrowserOrchestratorTestHelper
{
    public static BrowserOrchestrator CreateOrchestrator(
        IPageLoader pageLoader,
        ILinkExtractor linkExtractor,
        INavigationTreeBuilder treeBuilder,
        IReadableContentExtractor contentExtractor,
        IPageRenderer renderer,
        IInputHandler inputHandler,
        NavigationService navigationService,
        IPageCache? pageCache = null,
        IPreloadService? preloadService = null)
    {
        // Default to interactive so tests exercise the input loop
        inputHandler.IsInteractive.Returns(true);

        var scopeFactory = CreateScopeFactory();
        var browserSession = Substitute.For<IBrowserSessionControl>();
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(Domain.Enums.Browser.ThemeName.Phosphor);
        var resizeDetector = Substitute.For<IResizeDetector>();
        var browserConfig = Options.Create(new BrowserConfiguration());
        var logger = Substitute.For<ILogger<BrowserOrchestrator>>();

        var effectivePageCache = pageCache ?? Substitute.For<IPageCache>();
        var effectivePreloadService = preloadService ?? Substitute.For<IPreloadService>();

        var pipeline = CreatePipeline(
            pageLoader,
            linkExtractor,
            treeBuilder,
            contentExtractor,
            renderer,
            navigationService,
            scopeFactory,
            browserSession,
            effectivePageCache,
            effectivePreloadService,
            new BrowserConfiguration());

        return new BrowserOrchestrator(
            pageLoader,
            linkExtractor,
            treeBuilder,
            contentExtractor,
            renderer,
            inputHandler,
            navigationService,
            scopeFactory,
            browserSession,
            themeProvider,
            resizeDetector,
            effectivePageCache,
            effectivePreloadService,
            Substitute.For<IIdleDetector>(),
            Substitute.For<ICookieManager>(),
            Substitute.For<IHttpCookieRefresher>(),
            browserConfig,
            logger,
            pipeline,
            Substitute.For<ILayoutVariantProvider>(),
            CreateDockSpotlight());
    }

    /// <summary>
    /// DockSpotlight whose session is never docked, so the spotlight enqueues
    /// nothing and orchestrator tests stay free of background page access.
    /// </summary>
    public static DockSpotlight CreateDockSpotlight()
    {
        var session = Substitute.For<IBrowserSession>();
        session.IsDocked.Returns(false);
        session.HasActiveBrowser.Returns(false);
        return new DockSpotlight(
            session,
            Substitute.For<IPageAccessQueue>(),
            Substitute.For<ILogger<DockSpotlight>>());
    }

    /// <summary>
    /// Creates a PageLoadPipeline with test dependencies.
    /// Shared by tests that construct BrowserOrchestrator directly.
    /// </summary>
    public static PageLoadPipeline CreatePipeline(
        IPageLoader pageLoader,
        ILinkExtractor linkExtractor,
        INavigationTreeBuilder treeBuilder,
        IReadableContentExtractor contentExtractor,
        IPageRenderer renderer,
        NavigationService navigationService,
        IServiceScopeFactory scopeFactory,
        IBrowserSessionControl browserSession,
        IPageCache pageCache,
        IPreloadService preloadService,
        BrowserConfiguration? browserConfig = null)
    {
        return new PageLoadPipeline(
            pageLoader,
            linkExtractor,
            treeBuilder,
            contentExtractor,
            Substitute.For<IRssFeedDetector>(),
            renderer,
            navigationService,
            scopeFactory,
            browserSession,
            pageCache,
            preloadService,
            Substitute.For<ICookieManager>(),
            Substitute.For<IThemeProvider>(),
            browserConfig ?? new BrowserConfiguration(),
            Substitute.For<ILogger<PageLoadPipeline>>());
    }

    private static IServiceScopeFactory CreateScopeFactory()
    {
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

        return scopeFactory;
    }
}
