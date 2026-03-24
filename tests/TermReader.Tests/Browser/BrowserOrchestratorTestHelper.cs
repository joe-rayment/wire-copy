// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Tests.Browser;

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
            pageCache ?? Substitute.For<IPageCache>(),
            preloadService ?? Substitute.For<IPreloadService>(),
            Substitute.For<IIdleDetector>(),
            Substitute.For<ICookieManager>(),
            browserConfig,
            logger);
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
