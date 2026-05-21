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
public class PodcastCommandHandlerTests
{
    private readonly NavigationService _navigationService;
    private readonly IInputHandler _inputHandler;
    private readonly ITtsService _ttsService;
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
        _ttsService = Substitute.For<ITtsService>();
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

    #region API Key Validation (Saved Key)

    [Fact]
    public async Task HandleGeneratePodcast_SavedKeyInvalid_ClearsKeyAndProceeds()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false);
        _settingsStore.Get("OpenAiApiKey").Returns("old-invalid-key");

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid("Key expired", "invalid_key"));

        // TTS stays unconfigured → no cache analysis screen
        // Queue: [GoBack] → consumed by confirmation screen
        SetupInputQueue(Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.Received(1).Remove("OpenAiApiKey");
        _ttsService.Received(1).SetApiKeyOverride("old-invalid-key");
        _ttsService.Received(1).SetApiKeyOverride(string.Empty);
    }

    [Fact]
    public async Task HandleGeneratePodcast_SavedKeyValid_UsesKeyWithoutClearing()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false, true);
        _settingsStore.Get("OpenAiApiKey").Returns("valid-key");

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Valid());

        SetupInstantCacheAnalysis();

        // TTS becomes configured → cache analysis shown (1 call abandoned), then confirmation
        // Queue: [GoBack, GoBack] → 1st abandoned by cache analysis, 2nd for confirmation
        SetupInputQueue(Cmd(CommandType.GoBack), Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Remove("OpenAiApiKey");
    }

    [Fact]
    public async Task HandleGeneratePodcast_SavedKeyValidationTimesOut_ProceedsWithSavedKey()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false, true);
        _settingsStore.Get("OpenAiApiKey").Returns("maybe-valid-key");

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns<TtsValidationResult>(_ =>
                throw new OperationCanceledException("Validation timed out"));

        SetupInstantCacheAnalysis();

        // Queue: [GoBack, GoBack] → 1st abandoned by cache analysis, 2nd for confirmation
        SetupInputQueue(Cmd(CommandType.GoBack), Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Remove("OpenAiApiKey");
        _ttsService.Received(1).SetApiKeyOverride("maybe-valid-key");
    }

    #endregion

    #region Confirmation Screen

    [Fact]
    public async Task HandleGeneratePodcast_UserCancelsAtConfirmation_DoesNotStartGeneration()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);
        SetupInstantCacheAnalysis();

        // Queue: [GoBack, GoBack] → 1st abandoned by cache analysis, 2nd for confirmation
        SetupInputQueue(Cmd(CommandType.GoBack), Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
        await _orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_UserConfirmsGeneration_CallsOrchestrator()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);
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
    public async Task HandleGeneratePodcast_NoTtsKey_ModalSPressEntersSetupAndSaves()
    {
        // workspace-yib5 Phase 5: pressing `p` without a TTS key now pops an
        // inline modal BEFORE the slow cache-analysis pass. Pressing `s` on
        // that modal deep-links into HandleSetApiKey; a successful save
        // persists the key, triggers the resume-after-save callback, and the
        // outer retry loop re-enters generation now that the key is set —
        // proving the "zero extra keystrokes after paste + Enter" contract.
        SetupCollectionView();

        // Flip IsConfigured to true once SetApiKeyOverride is called with the
        // real key. This models the singleton TTS service: after a successful
        // save the override is set, so the retry skips the modal and proceeds
        // through cache-analysis (which we observe via AnalyzeCacheStatusAsync).
        _ttsService.IsConfigured.Returns(false);
        _ttsService.WhenForAnyArgs(s => s.SetApiKeyOverride(default!))
            .Do(call =>
            {
                if (call.Arg<string>() == "sk-test-key-123")
                {
                    _ttsService.IsConfigured.Returns(true);
                }
            });

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Valid());

        SetupInstantCacheAnalysis();

        // Modal: 's' → HandleSetApiKey → resume → retry → cache-analysis abandoned → confirmation: GoBack
        SetupInputQueue(
            new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 's' },
            Cmd(CommandType.GoBack),
            Cmd(CommandType.GoBack));

        _inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<Func<char, bool>?>())
            .Returns("sk-test-key-123");

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _ttsService.Received().SetApiKeyOverride("sk-test-key-123");
        _settingsStore.Received(1).Set("OpenAiApiKey", "sk-test-key-123", encrypt: true);

        // The retry actually proceeded into cache-analysis — proving the
        // resume callback fired and the outer loop re-entered RunGeneratePodcastAttempt.
        await _orchestrator.Received().AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_NoTtsKey_ApiKeyValidationFails_DoesNotPersist()
    {
        // workspace-yib5 Phase 5: on a failed auth probe, the key is cleared
        // and resumeAfterSave is NOT invoked — the user falls back to the
        // "set up cancelled" status line.
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false);

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid("Invalid key", "invalid_key"));

        // Modal: 's' → HandleSetApiKey → bad key → cancel (no resume)
        SetupInputQueue(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 's' });

        _inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<Func<char, bool>?>())
            .Returns("bad-key");

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Set("OpenAiApiKey", Arg.Any<string>(), Arg.Any<bool>());
        _ttsService.Received().SetApiKeyOverride(string.Empty);
    }

    [Fact]
    public async Task HandleGeneratePodcast_NoTtsKey_ModalEscCancelsGenerate()
    {
        // workspace-yib5 Phase 5: pressing Esc on the missing-key modal sets
        // the status-line message and exits the generate flow without
        // entering cache-analysis or HandleSetApiKey.
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false);

        SetupInputQueue(Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        await _inputHandler.DidNotReceive().PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<Func<char, bool>?>());
        await _orchestrator.DidNotReceive().AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_UnhandledKey_StaysOnConfirmationScreen()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false);

        // Send an unhandled key (MoveDown), then GoBack to exit.
        // If the unhandled key caused exit, the GoBack would never be consumed
        // and the orchestrator would not be called with GeneratePodcast.
        var unhandledCmd = new NavigationCommand { Type = CommandType.MoveDown, RawKeyChar = 'j' };
        SetupInputQueue(unhandledCmd, Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        // Should have rendered (from the GoBack exit path), proving we stayed on the screen
        _renderCalled.Should().BeTrue();
        // Should NOT have called GeneratePodcast (we exited via GoBack, not Enter)
        await _orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_MultipleUnhandledKeys_StaysOnConfirmationScreen()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false);

        // Send multiple unhandled keys, then GoBack
        var keys = new[]
        {
            new NavigationCommand { Type = CommandType.MoveDown, RawKeyChar = 'j' },
            new NavigationCommand { Type = CommandType.MoveUp, RawKeyChar = 'k' },
            new NavigationCommand { Type = CommandType.MoveDown, RawKeyChar = 'x' },
            Cmd(CommandType.GoBack),
        };
        SetupInputQueue(keys);

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region Cache Analysis Screen

    [Fact]
    public async Task HandleGeneratePodcast_CacheAnalysisCompletes_PassesToConfirmation()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);

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

        // Queue: 1=cache analysis (abandoned, analysis wins), 2=GoBack at confirmation
        SetupInputQueue(Cmd(CommandType.GoBack), Cmd(CommandType.GoBack));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        await _orchestrator.Received(1).AnalyzeCacheStatusAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<ContentExtractionProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleGeneratePodcast_CacheAnalysisFails_ContinuesWithoutAnalysis()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);

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
        _ttsService.IsConfigured.Returns(true);

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
        _ttsService.IsConfigured.Returns(true);
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
        _ttsService.IsConfigured.Returns(true);
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
        _ttsService.IsConfigured.Returns(true);
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
        _ttsService.IsConfigured.Returns(true);
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
        //   GoBack on progress screen (cancel progress).
        // GoBack wins the race because generation never completes.
        SetupGenerateInputQueue(
            Cmd(CommandType.GoBack));

        _inputHandler.PromptForInputAsync(
            Arg.Is<string>(s => s.Contains("Cancel")),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>())
            .Returns("y");

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    #endregion

    #region Error State Recovery

    [Fact]
    public async Task HandleGeneratePodcast_AuthError401_ClearsApiKeyAndShowsError()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);
        SetupInstantCacheAnalysis();

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(PodcastResult.Failure("HTTP 401 Unauthorized"));

        // After navigating to Generate + dismissing local-only warning:
        //   progress (abandoned) + error dismiss
        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.Received(1).Remove("OpenAiApiKey");
        _ttsService.Received(1).SetApiKeyOverride(string.Empty);
    }

    [Fact]
    public async Task HandleGeneratePodcast_AuthError403_ClearsApiKey()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);
        SetupInstantCacheAnalysis();

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(PodcastResult.Failure("HTTP 403 Forbidden"));

        // After navigating to Generate + dismissing local-only warning:
        //   progress (abandoned) + error dismiss
        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.Received(1).Remove("OpenAiApiKey");
    }

    [Fact]
    public async Task HandleGeneratePodcast_NotConfiguredError_ClearsApiKey()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);
        SetupInstantCacheAnalysis();

        _orchestrator.GeneratePodcastAsync(
            Arg.Any<Collection>(),
            Arg.Any<IProgress<PodcastProgress>>(),
            Arg.Any<CancellationToken>())
            .Returns(PodcastResult.Failure("TTS not configured"));

        // After navigating to Generate + dismissing local-only warning:
        //   progress (abandoned) + error dismiss
        SetupGenerateInputQueue(
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.ActivateLink));

        await PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, CancellationToken.None);

        _settingsStore.Received(1).Remove("OpenAiApiKey");
        _ttsService.Received(1).SetApiKeyOverride(string.Empty);
    }

    [Fact]
    public async Task HandleGeneratePodcast_NonAuthError_DoesNotClearApiKey()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(true);
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
        _ttsService.DidNotReceive().SetApiKeyOverride(string.Empty);
    }

    #endregion

    #region GCS Bucket Persistence

    [Fact]
    public async Task HandleGeneratePodcast_SavedBucketName_LoadedFromSettings()
    {
        SetupCollectionView();
        _ttsService.IsConfigured.Returns(false);
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
        _ttsService.IsConfigured.Returns(false);
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
        serviceProvider.GetService(typeof(ITtsService)).Returns(_ttsService);
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
        _ttsService.IsConfigured.Returns(false);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns<NavigationCommand>(_ => throw new OperationCanceledException());

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PodcastCommandHandler.HandleGeneratePodcast(_ctx, _options, cts.Token));
        ex.Should().BeAssignableTo<OperationCanceledException>();
    }

    #endregion

    #region Helpers

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
        serviceProvider.GetService(typeof(ITtsService)).Returns(_ttsService);
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
        serviceProvider.GetService(typeof(ITtsService)).Returns(_ttsService);
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
