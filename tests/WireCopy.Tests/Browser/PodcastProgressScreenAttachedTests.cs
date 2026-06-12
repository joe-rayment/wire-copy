// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-vkhr Phase D round-trip contract: when the modal re-attaches to
/// an existing background job via <see cref="PodcastProgressScreens.ShowProgressScreenAttachedAsync"/>,
/// it MUST subscribe to the live manager state and return the manager's
/// task result — never restart generation. The bead's required test:
/// "drive the input loop with p → D → ... → Shift+P → assert the modal
/// shows the up-to-date progress (no work restarted)."
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class PodcastProgressScreenAttachedTests
{
    private readonly IInputHandler _inputHandler = Substitute.For<IInputHandler>();
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;

    public PodcastProgressScreenAttachedTests()
    {
        _options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 35,
            MaxContentWidth = 100,
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(navLogger);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(sp);
        scopeFactory.CreateScope().Returns(scope);

        _ctx = new CommandContext
        {
            NavigationService = navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = _inputHandler,
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(navigationService, themeProvider),
            ThemeProvider = themeProvider,
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
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        // Default: the modal sits forever waiting for input. Individual tests
        // override this when they want to drive specific keystrokes.
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new TaskCompletionSource<NavigationCommand>().Task);
    }

    [Fact]
    public async Task Attached_CompletedJobInManager_ReturnsResult_WithoutRestartingWork()
    {
        // Pre-populated manager with an already-finished task. This is the
        // exact state of "user detached at 80%, generation finished while
        // detached, user pressed Shift+P": the manager has the completed
        // task and the last snapshot from before detach.
        var manager = new PodcastBackgroundJobManager();
        var collection = Collection.Create("RoundTrip Test");
        collection.AddItem("https://example.com/a", "A");
        collection.AddItem("https://example.com/b", "B");
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        manager.StartJob(collection, targets: null, tcs.Task, cts);
        manager.ReportProgress(new PodcastProgress
        {
            Phase = PodcastPhase.Publishing,
            PercentComplete = 95,
            CurrentArticle = 2,
            TotalArticles = 2,
        });

        var expected = PodcastResult.Successful(
            feedUrl: "https://example.com/feed.xml",
            localFilePath: "/tmp/test.m4a",
            totalDuration: TimeSpan.FromMinutes(7),
            articlesProcessed: 2,
            articlesFailed: 0,
            fileSizeBytes: 2_048_000,
            articlesCached: 0,
            totalCost: 0.05m);
        tcs.SetResult(expected);

        var result = await PodcastProgressScreens.ShowProgressScreenAttachedAsync(
            _ctx, _options, manager, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.FeedUrl.Should().Be("https://example.com/feed.xml");
        result.ArticlesProcessed.Should().Be(2);
        // The fact that we returned a result that matches the manager's task
        // proves we subscribed to it — the function takes no orchestrator
        // reference, so there is no other source for the result.

        // After the modal handles completion, Clear must have been called so
        // the status-bar badge stops showing "Generating" forever (Phase D
        // bead acceptance criterion).
        manager.HasActiveJob.Should().BeFalse(
            "the modal must Clear() the manager after handling completion — "
            + "otherwise the status-bar badge keeps reading 'Generating 100%' "
            + "after the run finishes");
    }

    [Fact]
    public async Task Attached_NoActiveJob_ReturnsNullImmediately()
    {
        // A manager with no job → re-attach is a no-op. The bead specifies
        // HandleRestorePodcastModal short-circuits when HasActiveJob is false;
        // this is the same guard at the lower level.
        var manager = new PodcastBackgroundJobManager();

        var result = await PodcastProgressScreens.ShowProgressScreenAttachedAsync(
            _ctx, _options, manager, CancellationToken.None);

        result.Should().BeNull();
        manager.HasActiveJob.Should().BeFalse();
    }

    // ---- workspace-m8es.2: GoBack detaches; cancel is the explicit 'x' ----

    [Fact]
    public async Task Attached_GoBack_WhileRunning_RedetachesWithoutCancelling()
    {
        var manager = new PodcastBackgroundJobManager();
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/a", "A");
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        manager.StartJob(collection, targets: null, tcs.Task, cts);

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.GoBack });

        var result = await PodcastProgressScreens.ShowProgressScreenAttachedAsync(
            _ctx, _options, manager, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().BeNull("backing out detaches instead of collecting a result");
        cts.IsCancellationRequested.Should().BeFalse(
            "Esc/b must NEVER cancel the run — that is the whole point of workspace-m8es.2");
        manager.HasActiveJob.Should().BeTrue("the job stays registered for the next Shift+P");
        await _inputHandler.DidNotReceive().PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attached_CancelRun_WithConfirm_CancelsTheRun()
    {
        var manager = new PodcastBackgroundJobManager();
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/a", "A");
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
        manager.StartJob(collection, targets: null, tcs.Task, cts);

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.CancelRun });
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("y");

        var result = await PodcastProgressScreens.ShowProgressScreenAttachedAsync(
            _ctx, _options, manager, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().BeNull();
        cts.IsCancellationRequested.Should().BeTrue(
            "'x' + confirm is the deliberate cancel path");
    }

    [Fact]
    public async Task Attached_CancelRun_Declined_KeepsRunning()
    {
        var manager = new PodcastBackgroundJobManager();
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/a", "A");
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        manager.StartJob(collection, targets: null, tcs.Task, cts);

        var prompted = new TaskCompletionSource();
        var inputCalls = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ++inputCalls == 1
                ? Task.FromResult(new NavigationCommand { Type = CommandType.CancelRun })
                : new TaskCompletionSource<NavigationCommand>().Task);
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { prompted.TrySetResult(); return "n"; });

        var modalTask = PodcastProgressScreens.ShowProgressScreenAttachedAsync(
            _ctx, _options, manager, CancellationToken.None);

        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.IsCancellationRequested.Should().BeFalse("a declined confirm changes nothing");

        // Finish the run normally — the modal must still collect the result.
        tcs.SetResult(PodcastResult.Successful(
            feedUrl: null,
            localFilePath: "/tmp/t.m4a",
            totalDuration: TimeSpan.FromSeconds(30),
            articlesProcessed: 1,
            articlesFailed: 0,
            fileSizeBytes: 1024,
            articlesCached: 0,
            totalCost: 0m));

        var result = await modalTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Attached_LivesProgressEvent_UpdatesContextProgressFlag()
    {
        // The attached modal sets ctx.IsPodcastGenerating while the run is
        // alive — proves it subscribes to the manager and forwards live
        // events into the CommandContext (drives the in-collection CTA
        // badge percent). We assert by completing the task immediately so
        // the function exits, but the IsPodcastGenerating flag is asserted
        // mid-flight by hooking into ProgressUpdated.
        var manager = new PodcastBackgroundJobManager();
        var collection = Collection.Create("Test");
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        manager.StartJob(collection, targets: null, tcs.Task, cts);

        var observedGenerating = false;
        manager.ProgressUpdated += (_, _) =>
        {
            // Inside the event the context flag should be true.
            observedGenerating = _ctx.IsPodcastGenerating;
        };

        // Kick off the attached modal, then immediately deliver a progress
        // tick + completion so it exits.
        var modalTask = PodcastProgressScreens.ShowProgressScreenAttachedAsync(
            _ctx, _options, manager, CancellationToken.None);

        // Give the modal a moment to register its ProgressUpdated handler
        // and set IsPodcastGenerating = true.
        await Task.Delay(50);
        manager.ReportProgress(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            PercentComplete = 50,
            CurrentArticle = 1,
            TotalArticles = 1,
        });

        tcs.SetResult(PodcastResult.Successful(
            feedUrl: null,
            localFilePath: "/tmp/t.m4a",
            totalDuration: TimeSpan.FromSeconds(30),
            articlesProcessed: 1,
            articlesFailed: 0,
            fileSizeBytes: 1024,
            articlesCached: 0,
            totalCost: 0m));

        var result = await modalTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().NotBeNull();
        observedGenerating.Should().BeTrue(
            "during the run, ctx.IsPodcastGenerating must be true — this "
            + "drives the in-collection 'GENERATE PODCAST' CTA into its "
            + "spinning-progress state");
        _ctx.IsPodcastGenerating.Should().BeFalse(
            "after the modal exits, the flag is cleared so the CTA returns "
            + "to its idle state");
    }
}
