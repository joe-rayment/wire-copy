// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WireCopy.Application.DTOs;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Audio;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for PodcastCommandHandler screen flows: guard clauses, cache analysis,
/// confirmation, progress, completion, and error screens.
/// </summary>
/// <remarks>
/// Uses a queue-based input handler with Task.Yield() to avoid infinite loops in
/// animation screens. The yield ensures synchronously-complete background tasks
/// (analysis, generation) win Task.WhenAny races, while queued commands are
/// consumed sequentially. Once exhausted, WaitForInputAsync blocks forever
/// (respecting cancellation), letting background tasks complete naturally.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ConsoleSerialCollection.Name)] // the narrator-readiness tests capture Console.Out
public class PodcastCommandHandlerTests
{
    private readonly NavigationService _navigationService;
    private readonly IInputHandler _inputHandler;
    private readonly IPodcastNarrator _narrator;
    private readonly IPodcastOrchestrator _orchestrator;
    private readonly IUserSettingsStore _settingsStore;
    private readonly GcsConfiguration _gcsConfig;
    private readonly IAudioAssembler _audioAssembler;
    private readonly IPreloadService _preloadService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private bool _renderCalled;

    public PodcastCommandHandlerTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        _navigationService.NavigateTo(page);

        _inputHandler = Substitute.For<IInputHandler>();
        // workspace-c3ec4.4: narration readiness is audio-engine's answer, not a local key check.
        _narrator = Substitute.For<IPodcastNarrator>();
        _narrator.EngineName.Returns("openai");
        _narrator.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(new NarratorStatus { Ready = true, Message = "audio-engine ready" });
        _orchestrator = Substitute.For<IPodcastOrchestrator>();
        _audioAssembler = Substitute.For<IAudioAssembler>();
        _audioAssembler.ValidatePrerequisitesAsync(Arg.Any<CancellationToken>()).Returns(true);
        _settingsStore = Substitute.For<IUserSettingsStore>();
        _gcsConfig = new GcsConfiguration();
        _preloadService = Substitute.For<IPreloadService>();

        // Preload complete → skip cache-wait screen
        _preloadService.GetProgress().Returns(new PreloadProgress
        {
            TotalCacheableLinks = 0,
            CachedCount = 0,
            NeedsBrowserCount = 0,
        });

        var scopeFactory = CreateScopeFactory();

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var lineCacheManager = new LineCacheManager(_navigationService, themeProvider);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 80,
        };

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = _inputHandler,
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = _preloadService,
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
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
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    #region Guard Clauses

    [Fact]
    public async Task HandleGeneratePodcast_NotInCollectionItemsView_SetsStatusAndReturns()
    {
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleGeneratePodcast_NullCollection_SetsStatusAndReturns()
    {
        _navigationService.SetViewMode(ViewMode.CollectionItems);

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleGeneratePodcast_EmptyCollection_SetsStatusAndReturns()
    {
        var collection = Collection.Create("Empty");
        _navigationService.EnterCollection(collection);

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region Cancel-before-progress (workspace-ny44 retired the confirmation screen)

    [Fact]
    public async Task HandleGeneratePodcast_UserCancelsAtCostGate_DoesNotStartGeneration()
    {
        // workspace-reym + ny44: the confirmation screen is gone. The
        // remaining cancel-before-progress affordance is the cost-gate
        // modal — which only appears when the pre-flight gate is actually
        // armed. Two paths trigger it:
        //   - AlwaysShow=true (user opted into always-confirm), OR
        //   - collection.Items.Count > ArticleThreshold (default 50 since
        //     workspace-reym).
        // Use AlwaysShow=true so we don't need a 51-item collection just
        // to exercise the cancel-at-gate contract.
        SetupCollectionView();
        _settingsStore.Get("PodcastCostGateAlwaysShow").Returns("true");

        // Force a high-cost analysis (10 articles + $5 estimate) so the
        // gate also fires by cost (defense-in-depth — AlwaysShow alone
        // would suffice).
        var analysis = new CacheAnalysis
        {
            TotalArticles = 10,
            CachedArticles = 0,
            UncachedArticles = 10,
            EstimatedCost = 5.00m,
            ArticleStatuses = new List<Application.DTOs.Podcast.ArticleCacheStatus>(),
        };
        _orchestrator.AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(analysis);

        // workspace-reym: the standalone "Analyzing Articles" screen is
        // gone, so the user only gets to press Esc once — on the cost-gate
        // modal itself.
        SetupInputQueue(Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
        await _orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_FullConfig_ReachesOrchestrator_LegacyInputQueue()
    {
        // workspace-kuu7: this test originally asserted "user confirms generation"
        // by navigating through the confirmation screen rows. After Phase 1 removed
        // that screen, the test name and its SetupGenerateInputQueue preamble (5
        // MoveDowns + 2 ActivateLinks for the row navigation) are anachronisms —
        // the extra inputs are now silently consumed by the progress / completion
        // screens. The new test that PROVES confirmation is gone is
        // HandleGeneratePodcast_WithFullConfig_SkipsConfirmationScreen above; this
        // remaining test is kept to verify the orchestrator still gets called with
        // the legacy input queue (a back-compat sanity check that the extra
        // keystrokes don't blow up the new flow).
        SetupCollectionView();
        SetupInstantCacheAnalysis();

        var result = PodcastResult.Successful(
            feedUrl: null,
            localFilePath: "/tmp/test.m4b",
            totalDuration: TimeSpan.FromMinutes(10),
            articlesProcessed: 2,
            articlesFailed: 0,
            fileSizeBytes: 1024 * 1024);

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(result);

        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        await _orchestrator.Received(1).GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Cache Analysis Screen

    [Fact]
    public async Task HandleGeneratePodcast_CacheAnalysisRunsOnlyWhenGateArmed()
    {
        // workspace-reym: AnalyzeCacheStatusAsync no longer runs as a
        // mandatory pre-flight UI screen — it only runs when the cost-gate
        // is plausibly going to fire (AlwaysShow=true OR Items.Count >
        // ArticleThreshold). When neither is true the handler skips
        // straight to the Progress screen, which extracts inline via
        // GeneratePodcastAsync. This test pins that contract: when
        // AlwaysShow is enabled, analysis runs; otherwise it doesn't.
        SetupCollectionView();
        _settingsStore.Get("PodcastCostGateAlwaysShow").Returns("true");

        var analysis = new CacheAnalysis
        {
            TotalArticles = 2,
            CachedArticles = 1,
            UncachedArticles = 1,
            EstimatedCost = 0.05m,
            ArticleStatuses =
            [
                new Application.DTOs.Podcast.ArticleCacheStatus
                {
                    Url = "https://a.com", Title = "A", IsCached = true, EstimatedCost = 0m,
                },
                new Application.DTOs.Podcast.ArticleCacheStatus
                {
                    Url = "https://b.com", Title = "B", IsCached = false, EstimatedCost = 0.05m,
                },
            ],
        };

        _orchestrator.AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(analysis);

        // Cost-gate cancel.
        SetupInputQueue(Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        await _orchestrator.Received(1).AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_CacheAnalysisSkippedOnDefaultPath()
    {
        // workspace-reym companion: the happy path (small list, AlwaysShow
        // unset) bypasses AnalyzeCacheStatusAsync entirely. The orchestrator
        // extracts inline during GeneratePodcastAsync — same total work,
        // but no separate pre-flight screen and no silent analysis hang.
        SetupCollectionView();
        SetupInstantCacheAnalysis();

        var result = PodcastResult.Successful(
            feedUrl: null,
            localFilePath: "/tmp/test.m4b",
            totalDuration: TimeSpan.FromMinutes(1),
            articlesProcessed: 2,
            articlesFailed: 0,
            fileSizeBytes: 1024);
        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(result);

        // Progress screen then completion-screen dismiss.
        SetupInputQueue(Cmd(CommandType.ActivateLink), Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        await _orchestrator.DidNotReceive().AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_CacheAnalysisFails_ContinuesWithoutAnalysis()
    {
        SetupCollectionView();

        _orchestrator.AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        // Queue: 1=cache analysis (abandoned, faulted task wins), 2=GoBack at confirmation
        SetupInputQueue(Cmd(CommandType.GoBack), Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleGeneratePodcast_UserSkipsCacheAnalysis_ProceedsWithoutAnalysis()
    {
        SetupCollectionView();

        // Analysis responds to cancellation but never completes on its own
        _orchestrator.AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = callInfo.ArgAt<CancellationToken>(2);
                var tcs = new TaskCompletionSource<CacheAnalysis>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            });

        // Queue: 1=GoBack (user skips analysis — wins race since analysis never completes),
        //        2=GoBack at confirmation
        SetupInputQueue(Cmd(CommandType.GoBack), Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region Progress Screen

    [Fact]
    public async Task HandleGeneratePodcast_GenerationSucceeds_ShowsCompletionScreen()
    {
        SetupCollectionView();
        SetupInstantCacheAnalysis();

        var result = PodcastResult.Successful(
            feedUrl: null,
            localFilePath: "/tmp/podcast.m4b",
            totalDuration: TimeSpan.FromMinutes(15),
            articlesProcessed: 2,
            articlesFailed: 0,
            fileSizeBytes: 2 * 1024 * 1024);

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(result);

        // After navigating to Generate + dismissing local-only warning:
        //   progress (abandoned) + completion dismiss
        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        await _orchestrator.Received(1).GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_GenerationFails_ShowsErrorScreen()
    {
        SetupCollectionView();
        SetupInstantCacheAnalysis();

        var failResult = PodcastResult.Failure(
            "FFmpeg not found",
            failedArticleDetails:
            [
                new ArticleFailure { Title = "Article 1", Url = "https://a.com", Reason = "FFmpeg error" },
            ]);

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(failResult);

        // After navigating to Generate + dismissing local-only warning:
        //   progress (abandoned) + error dismiss
        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleGeneratePodcast_GenerationThrows_ShowsErrorScreen()
    {
        SetupCollectionView();
        SetupInstantCacheAnalysis();

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("TTS service unavailable"));

        // After navigating to Generate + dismissing local-only warning:
        //   progress (abandoned) + error dismiss
        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleGeneratePodcast_UserCancelsDuringProgress_CancelsGeneration()
    {
        SetupCollectionView();
        SetupInstantCacheAnalysis();

        // Generation responds to cancellation
        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.ArgAt<CancellationToken>(2);
                await Task.Delay(Timeout.Infinite, ct);
                return PodcastResult.Failure("cancelled");
            });

        // After navigating to Generate + dismissing local-only warning:
        //   GoBack on progress screen (cancel progress), then 'y' on the
        //   visual confirm modal (workspace-nahg item 1).
        // GoBack wins the race because generation never completes.
        SetupGenerateInputQueue(
            Cmd(CommandType.GoBack),
            new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region Error State Recovery

    [Fact]
    public async Task HandleGeneratePodcast_NonAuthError_DoesNotTouchSavedSettings()
    {
        SetupCollectionView();
        SetupInstantCacheAnalysis();

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(PodcastResult.Failure("FFmpeg encoding failed"));

        // After navigating to Generate + dismissing local-only warning:
        //   progress (abandoned) + error dismiss
        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Remove("OpenAiApiKey");
    }

    #endregion

    #region GCS Bucket Persistence

    [Fact]
    public async Task HandleGeneratePodcast_SavedBucketName_LoadedFromSettings()
    {
        SetupCollectionView();
        SetNarratorNotReady();
        _settingsStore.Get("GcsBucketName").Returns("my-podcast-bucket");

        // No cache analysis (not configured).
        // ValidateAndBootstrapBucketAsync spinner consumes 1 call (abandoned),
        // then confirmation → GoBack.
        SetupInputQueue(Cmd(CommandType.GoBack), Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _gcsConfig.BucketName.Should().Be("my-podcast-bucket");
    }

    [Fact]
    public async Task HandleGeneratePodcast_InvalidSavedBucketName_Ignored()
    {
        SetupCollectionView();
        SetNarratorNotReady();
        _settingsStore.Get("GcsBucketName").Returns("x"); // too short

        SetupInputQueue(Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _gcsConfig.BucketName.Should().BeNull();
    }

    #endregion

    #region GCS Credential Failures (d0dl / erj0)

    [Fact]
    public async Task HandleGeneratePodcast_GcsValidationThrows_DoesNotCrash()
    {
        // GCS validation throws (simulating missing credentials)
        var cloudStorage = Substitute.For<ICloudStorageClient>();
        cloudStorage.ValidateConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("No GCP credentials found"));

        var failingScopeFactory = CreateScopeFactoryWith(cloudStorage);
        var gcsConfig = new GcsConfiguration { BucketName = "test-bucket" };
        var ctx = CreateCommandContextWith(failingScopeFactory, gcsConfig);

        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/article1", "Art1");
        ctx.NavigationService.EnterCollection(collection);

        SetupInputQueue(Cmd(CommandType.GoBack));

        // Should NOT throw — outer try/catch handles the error
        await PodcastCommandHandler.HandleGeneratePodcast(ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue("should render error screen, not crash");
    }

    [Fact]
    public async Task HandleGeneratePodcast_CloudStorageDiMissing_DoesNotCrash()
    {
        // Simulate missing ICloudStorageClient in DI (GetRequiredService throws)
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPodcastNarrator)).Returns(_narrator);
        serviceProvider.GetService(typeof(IPodcastOrchestrator)).Returns(_orchestrator);
        serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(_settingsStore);
        serviceProvider.GetService(typeof(IOptions<GcsConfiguration>))
            .Returns(Options.Create(new GcsConfiguration { BucketName = "test-bucket" }));
        serviceProvider.GetService(typeof(IAudioAssembler)).Returns(_audioAssembler);
        serviceProvider.GetService(typeof(ICloudStorageClient)).Returns(null as object);
        serviceProvider.GetService(typeof(IPodcastPublisher)).Returns(null as object);
        serviceProvider.GetService(typeof(IOptions<PodcastConfiguration>))
            .Returns(Options.Create(new PodcastConfiguration()));

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        var ctx = CreateCommandContextWith(factory);

        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/article1", "Art1");
        ctx.NavigationService.EnterCollection(collection);

        SetupInputQueue(Cmd(CommandType.GoBack));

        // Should NOT throw — outer try/catch handles the DI failure
        await PodcastCommandHandler.HandleGeneratePodcast(ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue("should show error, not crash");
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task HandleGeneratePodcast_CancellationTokenFired_Throws()
    {
        SetupCollectionView();
        SetNarratorNotReady();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns<NavigationCommand>(_ => throw new OperationCanceledException());

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, cts.Token));
        ex.Should().BeAssignableTo<OperationCanceledException>();
    }

    #endregion

    #region Narrator readiness (workspace-c3ec4.4)

    // Guards:   pressing p when audio-engine says it is not ready shows the error screen with
    //           audio-engine's own message and never starts generation.
    // Fails if: the handler ignores NarratorStatus.Ready, drops status.Message, or calls
    //           GeneratePodcastAsync anyway.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastCommandHandlerTests.HandleGeneratePodcast_NarratorNotReady_ShowsItsMessageAndStops"
    [Fact]
    public async Task HandleGeneratePodcast_NarratorNotReady_ShowsItsMessageAndStops()
    {
        SetupCollectionView();
        const string message = "audio-engine rejected the API key. Set it in Settings -> Podcast narration.";
        SetNarratorNotReady(message);

        // One key dismisses the error screen.
        SetupInputQueue(Cmd(CommandType.ActivateLink));

        var screen = await CaptureConsoleAsync(
            () => PodcastCommandHandler.RunGeneratePodcastAttempt(_ctx, _options, CancellationToken.None));

        screen.Result.Should().BeFalse("a not-ready narrator ends the attempt without a retry");
        StripAnsi(screen.Output).Should().Contain("Podcast generation failed");
        StripAnsi(screen.Output).Should().Contain("rejected the API key");
        StripAnsi(screen.Output).Should().Contain("Audio engine");
        await _orchestrator.DidNotReceive().AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>());
        await _orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>());
    }

    // Guards:   an audio-engine that never answers becomes a not-ready status that says it
    //           isn't running, instead of hanging the keypress or crashing.
    // Fails if: CheckNarratorAsync lets the OperationCanceledException escape, or its synthetic
    //           status is Ready or lacks the "isn't running" wording the classifier keys on.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastCommandHandlerTests.CheckNarratorAsync_CheckTimesOut_ReportsNotRunning"
    [Fact]
    public async Task CheckNarratorAsync_CheckTimesOut_ReportsNotRunning()
    {
        var narrator = Substitute.For<IPodcastNarrator>();
        narrator.CheckAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("no reply"));

        var status = await PodcastCommandHandler.CheckNarratorAsync(narrator, CancellationToken.None);

        status.Ready.Should().BeFalse();
        status.ErrorKind.Should().Be(NarrationErrorKind.Unreachable);
        status.Message.Should().StartWith("audio-engine");
        status.Message.Should().Contain("isn't running");
    }

    // Guards:   when audio-engine's selected engine is Chatterbox, the cost gate tells the user
    //           the run is local and free instead of quoting an OpenAI price.
    // Fails if: the handler stops passing engineIsLocal=true for EngineName "chatterbox"
    //           (or compares the name case-sensitively).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastCommandHandlerTests.HandleGeneratePodcast_ChatterboxEngine_CostGateSaysGeneratedLocally"
    [Fact]
    public async Task HandleGeneratePodcast_ChatterboxEngine_CostGateSaysGeneratedLocally()
    {
        SetupCollectionView();
        _narrator.EngineName.Returns("Chatterbox");
        _settingsStore.Get("PodcastCostGateAlwaysShow").Returns("true");

        // The gate only arms for a non-trivial job (same recipe as
        // HandleGeneratePodcast_UserCancelsAtCostGate_DoesNotStartGeneration).
        _orchestrator.AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(new CacheAnalysis
            {
                TotalArticles = 10,
                CachedArticles = 0,
                UncachedArticles = 10,
                EstimatedCost = 5.00m,
                ArticleStatuses = [],
            });

        // Esc on the cost-gate modal cancels the attempt.
        SetupInputQueue(Cmd(CommandType.GoBack));

        var screen = await CaptureConsoleAsync(
            () => PodcastCommandHandler.RunGeneratePodcastAttempt(_ctx, _options, CancellationToken.None));

        StripAnsi(screen.Output).Should().Contain("generated locally (Chatterbox)");
        await _orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Helpers

    private void SetNarratorNotReady(string message = "audio-engine isn't running at http://127.0.0.1:8080.")
    {
        _narrator.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(new NarratorStatus { Ready = false, Message = message });
    }

    /// <summary>Runs <paramref name="action"/> with Console.Out captured (class is in ConsoleSerialCollection).</summary>
    private static async Task<(bool Result, string Output)> CaptureConsoleAsync(Func<Task<bool>> action)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await action();
            return (result, sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string StripAnsi(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;?]*[A-Za-z]", string.Empty);

    private static NavigationCommand Cmd(CommandType type) =>
        new() { Type = type };

    /// <summary>
    /// Sets up WaitForInputAsync to return commands from a queue with Task.Yield() delay.
    /// The yield ensures synchronously-complete background tasks (analysis, generation)
    /// win Task.WhenAny races in animation loops. After the queue is exhausted, returns
    /// a never-completing task that respects cancellation.
    /// </summary>
    private void SetupInputQueue(params NavigationCommand[] commands)
    {
        var index = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var i = Interlocked.Increment(ref index) - 1;
                if (i < commands.Length)
                {
                    return YieldThen(commands[i]);
                }

                // Queue exhausted — block until cancelled
                var ct = callInfo.ArgAt<CancellationToken>(0);
                var tcs = new TaskCompletionSource<NavigationCommand>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            });
    }

    /// <summary>
    /// Prepends the keystrokes needed to navigate the row-based confirmation screen
    /// from row 0 (TtsKey) to the Generate row, press Enter to start generation, and
    /// confirm the local-only warning panel that appears when no GCS bucket is set.
    /// </summary>
    /// <remarks>
    /// Layout (no GCS client wired in tests): TtsKey, GcsBucket, OutputFolder, Voice,
    /// Model, Generate — so 5 MoveDowns reach Generate. ActivateLink on Generate
    /// triggers ShowLocalOnlyWarningAsync, which a second ActivateLink dismisses by
    /// choosing "GenerateLocally". The leading ActivateLink is consumed (and abandoned)
    /// by the cache-analysis screen's pendingKeyTask before its synchronously-complete
    /// AnalyzeCacheStatusAsync wins Task.WhenAny.
    /// </remarks>
    private void SetupGenerateInputQueue(params NavigationCommand[] postConfirmationCommands)
    {
        var preamble = new List<NavigationCommand>
        {
            // 1) Abandoned by cache-analysis screen (analysisTask wins WhenAny)
            Cmd(CommandType.ActivateLink),

            // 2) Navigate from TtsKey to Generate (5 rows down, no GcsKey row in tests)
            Cmd(CommandType.MoveDown),
            Cmd(CommandType.MoveDown),
            Cmd(CommandType.MoveDown),
            Cmd(CommandType.MoveDown),
            Cmd(CommandType.MoveDown),

            // 3) Enter on Generate row → opens LocalOnly warning (no GCS configured)
            Cmd(CommandType.ActivateLink),

            // 4) Enter on LocalOnly warning → returns GenerateLocally → start generation
            Cmd(CommandType.ActivateLink),
        };

        preamble.AddRange(postConfirmationCommands);
        SetupInputQueue([.. preamble]);
    }

    private static async Task<NavigationCommand> YieldThen(NavigationCommand cmd)
    {
        await Task.Delay(1);
        return cmd;
    }

    private Collection SetupCollectionView()
    {
        var collection = Collection.Create("Test Collection");
        collection.AddItem("https://example.com/article1", "First Article");
        collection.AddItem("https://example.com/article2", "Second Article");
        _navigationService.EnterCollection(collection);
        return collection;
    }

    private void SetupInstantCacheAnalysis()
    {
        _orchestrator.AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(new CacheAnalysis
            {
                TotalArticles = 2,
                CachedArticles = 2,
                UncachedArticles = 0,
                EstimatedCost = 0m,
                ArticleStatuses = [],
            });
    }

    private CommandContext CreateCommandContextWith(IServiceScopeFactory scopeFactory, GcsConfiguration? gcsConfig = null)
    {
        var navService = new NavigationService(Substitute.For<ILogger<NavigationService>>());
        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        navService.NavigateTo(page);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var lineCacheManager = new LineCacheManager(navService, themeProvider);

        return new CommandContext
        {
            NavigationService = navService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = _inputHandler,
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = _preloadService,
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
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
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    private IServiceScopeFactory CreateScopeFactoryWith(ICloudStorageClient cloudStorageOverride)
    {
        var publisher = Substitute.For<IPodcastPublisher>();
        publisher.GetExistingFeedUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var podcastConfig = new PodcastConfiguration();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPodcastNarrator)).Returns(_narrator);
        serviceProvider.GetService(typeof(IPodcastOrchestrator)).Returns(_orchestrator);
        serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(_settingsStore);
        serviceProvider.GetService(typeof(IOptions<GcsConfiguration>))
            .Returns(Options.Create(_gcsConfig));
        serviceProvider.GetService(typeof(IAudioAssembler)).Returns(_audioAssembler);
        serviceProvider.GetService(typeof(ICloudStorageClient)).Returns(cloudStorageOverride);
        serviceProvider.GetService(typeof(IPodcastPublisher)).Returns(publisher);
        serviceProvider.GetService(typeof(IOptions<PodcastConfiguration>))
            .Returns(Options.Create(podcastConfig));

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        return factory;
    }

    private IServiceScopeFactory CreateScopeFactory()
    {
        var cloudStorage = Substitute.For<ICloudStorageClient>();
        cloudStorage.ValidateConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CloudStorageValidationResult { IsValid = true });

        var publisher = Substitute.For<IPodcastPublisher>();
        publisher.GetExistingFeedUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        publisher.BootstrapFeedAsync(Arg.Any<PodcastMetadata>(), Arg.Any<CancellationToken>())
            .Returns(new FeedPublishResult
            {
                Success = true,
                FeedUrl = "https://storage.googleapis.com/test/feed.xml",
                EpisodesPublished = 0,
                PublishedAtUtc = DateTime.UtcNow,
            });

        var podcastConfig = new PodcastConfiguration();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPodcastNarrator)).Returns(_narrator);
        serviceProvider.GetService(typeof(IPodcastOrchestrator)).Returns(_orchestrator);
        serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(_settingsStore);
        serviceProvider.GetService(typeof(IOptions<GcsConfiguration>))
            .Returns(Options.Create(_gcsConfig));
        serviceProvider.GetService(typeof(IAudioAssembler)).Returns(_audioAssembler);
        serviceProvider.GetService(typeof(ICloudStorageClient)).Returns(cloudStorage);
        serviceProvider.GetService(typeof(IPodcastPublisher)).Returns(publisher);
        serviceProvider.GetService(typeof(IOptions<PodcastConfiguration>))
            .Returns(Options.Create(podcastConfig));

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        return factory;
    }

    #endregion
}
