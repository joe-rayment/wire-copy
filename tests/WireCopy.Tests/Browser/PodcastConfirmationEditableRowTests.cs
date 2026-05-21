// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the editable rows on the Generate Podcast confirmation screen
/// (workspace-urko). Each config row — output folder, voice, model — must
/// dispatch on Enter, persist via <see cref="IUserSettingsStore"/>, and update
/// in place. These tests cover the per-row dispatch helpers; full-screen
/// integration is covered by the live-test gate in the bead.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastConfirmationEditableRowTests
{
    private readonly IInputHandler _inputHandler;
    private readonly IUserSettingsStore _settingsStore;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;

    public PodcastConfirmationEditableRowTests()
    {
        _inputHandler = Substitute.For<IInputHandler>();
        _settingsStore = Substitute.For<IUserSettingsStore>();

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(navLogger);

        var ttsOptions = Substitute.For<IOptions<OpenAiTtsConfiguration>>();
        ttsOptions.Value.Returns(new OpenAiTtsConfiguration());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(_settingsStore);
        serviceProvider.GetService(typeof(IOptions<OpenAiTtsConfiguration>)).Returns(ttsOptions);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 30,
            MaxContentWidth = 80,
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

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
    }

    #region OutputFolder row

    [Fact]
    public async Task OutputFolder_NewPath_PersistsViaSettingsStore()
    {
        // User types a new folder; expect persistence under "PodcastOutputFolder".
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("/tmp/my-podcasts");

        var result = await PodcastConfirmationScreens.PromptAndSetOutputFolderAsync(
            _ctx, _settingsStore, "/old/path", CancellationToken.None);

        result.Should().Be("/tmp/my-podcasts");
        _settingsStore.Received(1).Set("PodcastOutputFolder", "/tmp/my-podcasts", Arg.Any<bool>());
    }

    [Fact]
    public async Task OutputFolder_EmptyInput_ReturnsNull_DoesNotPersist()
    {
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(string.Empty);

        var result = await PodcastConfirmationScreens.PromptAndSetOutputFolderAsync(
            _ctx, _settingsStore, "/old/path", CancellationToken.None);

        result.Should().BeNull("blank input is a no-op");
        _settingsStore.DidNotReceive().Set(
            Arg.Is<string>(k => k == "PodcastOutputFolder"),
            Arg.Any<string>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task OutputFolder_CancelledByEsc_ReturnsNull_DoesNotPersist()
    {
        // PromptForInputAsync returns null when the user presses Esc.
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns((string?)null);

        var result = await PodcastConfirmationScreens.PromptAndSetOutputFolderAsync(
            _ctx, _settingsStore, "/old/path", CancellationToken.None);

        result.Should().BeNull("Esc cancels and returns null");
        _settingsStore.DidNotReceive().Set(
            Arg.Is<string>(k => k == "PodcastOutputFolder"),
            Arg.Any<string>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task OutputFolder_Reset_RemovesOverride()
    {
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("reset");

        var result = await PodcastConfirmationScreens.PromptAndSetOutputFolderAsync(
            _ctx, _settingsStore, "/old/path", CancellationToken.None);

        result.Should().NotBeNull("reset must yield the default folder");
        _settingsStore.Received(1).Remove("PodcastOutputFolder");
    }

    #endregion

    #region Voice picker

    [Fact]
    public async Task Voice_EnterOnSelected_PersistsChoice()
    {
        // Press Enter immediately on the picker — selection defaults to the
        // current value, so "nova" should be persisted (nova is in the list).
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(NavCmd(CommandType.ActivateLink));

        var result = await PodcastConfirmationScreens.PromptAndPickVoiceAsync(
            _ctx, _options, _settingsStore, "nova", CancellationToken.None);

        result.Should().Be("nova");
        _settingsStore.Received(1).Set("OpenAiTtsVoice", "nova", Arg.Any<bool>());
    }

    [Fact]
    public async Task Voice_UpArrowThenEnter_PicksPreviousVoice()
    {
        // Default cursor lands on the current value ("nova", index 7). Up should
        // move to "onyx" (index 6). Enter then persists "onyx".
        var sequence = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.MoveUp),
            NavCmd(CommandType.ActivateLink),
        });

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => sequence.Dequeue());

        var result = await PodcastConfirmationScreens.PromptAndPickVoiceAsync(
            _ctx, _options, _settingsStore, "nova", CancellationToken.None);

        result.Should().Be("onyx");
        _settingsStore.Received(1).Set("OpenAiTtsVoice", "onyx", Arg.Any<bool>());
    }

    [Fact]
    public async Task Voice_Esc_ReturnsNull_DoesNotPersist()
    {
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(NavCmd(CommandType.GoBack));

        var result = await PodcastConfirmationScreens.PromptAndPickVoiceAsync(
            _ctx, _options, _settingsStore, "nova", CancellationToken.None);

        result.Should().BeNull();
        _settingsStore.DidNotReceive().Set(
            "OpenAiTtsVoice", Arg.Any<string>(), Arg.Any<bool>());
    }

    #endregion

    #region Model picker

    [Fact]
    public async Task Model_EnterOnSelected_PersistsChoice()
    {
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(NavCmd(CommandType.ActivateLink));

        var result = await PodcastConfirmationScreens.PromptAndPickModelAsync(
            _ctx, _options, _settingsStore, "tts-1", CancellationToken.None);

        result.Should().Be("tts-1");
        _settingsStore.Received(1).Set("OpenAiTtsModel", "tts-1", Arg.Any<bool>());
    }

    [Fact]
    public async Task Model_DownArrowThenEnter_PicksHd()
    {
        // Default cursor lands on "tts-1" (index 0). Down moves to "tts-1-hd" (index 1).
        var sequence = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.MoveDown),
            NavCmd(CommandType.ActivateLink),
        });

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => sequence.Dequeue());

        var result = await PodcastConfirmationScreens.PromptAndPickModelAsync(
            _ctx, _options, _settingsStore, "tts-1", CancellationToken.None);

        result.Should().Be("tts-1-hd");
        _settingsStore.Received(1).Set("OpenAiTtsModel", "tts-1-hd", Arg.Any<bool>());
    }

    [Fact]
    public async Task Model_Esc_ReturnsNull_DoesNotPersist()
    {
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(NavCmd(CommandType.GoBack));

        var result = await PodcastConfirmationScreens.PromptAndPickModelAsync(
            _ctx, _options, _settingsStore, "tts-1", CancellationToken.None);

        result.Should().BeNull();
        _settingsStore.DidNotReceive().Set(
            "OpenAiTtsModel", Arg.Any<string>(), Arg.Any<bool>());
    }

    #endregion

    #region Down-arrow navigation in picker

    [Fact]
    public async Task Voice_DownArrowThenEnter_PicksNextVoice()
    {
        // Default is "nova" (index 7 in the list). Down should wrap to next entry,
        // i.e. "sage" (index 8). Enter then persists "sage".
        var sequence = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.MoveDown),
            NavCmd(CommandType.ActivateLink),
        });

        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => sequence.Dequeue());

        var result = await PodcastConfirmationScreens.PromptAndPickVoiceAsync(
            _ctx, _options, _settingsStore, "nova", CancellationToken.None);

        result.Should().Be("sage");
        _settingsStore.Received(1).Set("OpenAiTtsVoice", "sage", Arg.Any<bool>());
    }

    #endregion

    private static NavigationCommand NavCmd(CommandType type, char? raw = null) =>
        new() { Type = type, RawKeyChar = raw };

    #region End-to-end row dispatch (drive ShowConfirmationScreenAsync)

    /// <summary>
    /// Drives <see cref="PodcastConfirmationScreens.ShowConfirmationScreenAsync"/> end-to-end
    /// to prove the OutputFolder row dispatch in
    /// <see cref="PodcastConfirmationScreens"/> (the ActivateLink branch at the row enum)
    /// actually invokes the prompt and persists via the settings store.
    /// </summary>
    [Fact]
    public async Task ShowConfirmation_NavigateToOutputFolderAndEdit_PersistsViaSettingsStore()
    {
        // Layout (no GCS client wired): TtsKey(0), GcsBucket(1), OutputFolder(2), Voice(3), Model(4), Generate(5).
        // workspace-sfhy: with TTS configured the default focus is Generate(5);
        // wrap-around MoveDown lands on TtsKey(0), then DOWN×2 reaches OutputFolder.
        var ttsService = Substitute.For<ITtsService>();
        ttsService.IsConfigured.Returns(true);

        var queue = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.MoveDown), // Generate(5) → TtsKey(0) (wrap)
            NavCmd(CommandType.MoveDown), // → GcsBucket(1)
            NavCmd(CommandType.MoveDown), // → OutputFolder(2)
            NavCmd(CommandType.ActivateLink), // dispatch: enters PromptAndSetOutputFolderAsync
            NavCmd(CommandType.GoBack), // exit confirmation after dispatch returns
        });
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => queue.Dequeue());

        // The output-folder prompt is delivered via PromptForInputAsync (text input).
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("/tmp/integration-output");

        var collection = Collection.Create("ITest");
        collection.AddItem("https://example.com/a", "A");
        var gcsConfig = new GcsConfiguration();

        var result = await PodcastConfirmationScreens.ShowConfirmationScreenAsync(
            _ctx,
            _options,
            collection,
            ttsService,
            gcsConfig,
            _settingsStore,
            gcsClient: null,
            cacheAnalysis: null,
            preflightBucketError: null,
            preflightFeedUrl: null,
            preflightFeedStatusNote: null,
            CancellationToken.None);

        // GoBack at the end of the queue → false (user did not confirm Generate)
        result.Should().BeFalse();
        _settingsStore.Received(1).Set(
            "PodcastOutputFolder", "/tmp/integration-output", Arg.Any<bool>());
    }

    /// <summary>
    /// Integration test: with TTS configured the default focus is Generate(5);
    /// wrap-around DOWN reaches TtsKey(0), then three more DOWNs land on Voice(3).
    /// On Voice, ActivateLink enters the picker; the picker's default selection lands
    /// on the current voice ("nova"). Down moves to "sage". Enter persists "sage".
    /// </summary>
    [Fact]
    public async Task ShowConfirmation_NavigateToVoiceAndPick_PersistsViaSettingsStore()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.IsConfigured.Returns(true);

        // Make ResolveCurrentVoice return "nova" so the picker default cursor lands there.
        _settingsStore.Get("OpenAiTtsVoice").Returns("nova");

        var queue = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.MoveDown),     // Generate(5) → TtsKey(0) (wrap)
            NavCmd(CommandType.MoveDown),     // → GcsBucket(1)
            NavCmd(CommandType.MoveDown),     // → OutputFolder(2)
            NavCmd(CommandType.MoveDown),     // → Voice(3)
            NavCmd(CommandType.ActivateLink), // dispatch → enter Voice picker
            NavCmd(CommandType.MoveDown),     // picker: nova(7) → sage(8)
            NavCmd(CommandType.ActivateLink), // picker: pick sage
            NavCmd(CommandType.GoBack),       // exit confirmation
        });
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => queue.Dequeue());

        var collection = Collection.Create("ITest");
        collection.AddItem("https://example.com/a", "A");
        var gcsConfig = new GcsConfiguration();

        var result = await PodcastConfirmationScreens.ShowConfirmationScreenAsync(
            _ctx,
            _options,
            collection,
            ttsService,
            gcsConfig,
            _settingsStore,
            gcsClient: null,
            cacheAnalysis: null,
            preflightBucketError: null,
            preflightFeedUrl: null,
            preflightFeedStatusNote: null,
            CancellationToken.None);

        result.Should().BeFalse();
        _settingsStore.Received(1).Set("OpenAiTtsVoice", "sage", Arg.Any<bool>());
    }

    /// <summary>
    /// Integration test: with TTS configured the default focus is Generate(5);
    /// wrap-around DOWN reaches TtsKey(0), then four more DOWNs land on Model(4).
    /// On Model, ActivateLink enters the picker; current model is "tts-1" so the
    /// picker default cursor sits on tts-1(0). Down moves to tts-1-hd(1). Enter persists.
    /// </summary>
    [Fact]
    public async Task ShowConfirmation_NavigateToModelAndPick_PersistsViaSettingsStore()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.IsConfigured.Returns(true);

        _settingsStore.Get("OpenAiTtsModel").Returns("tts-1");

        var queue = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.MoveDown),     // Generate(5) → TtsKey(0) (wrap)
            NavCmd(CommandType.MoveDown),     // → GcsBucket(1)
            NavCmd(CommandType.MoveDown),     // → OutputFolder(2)
            NavCmd(CommandType.MoveDown),     // → Voice(3)
            NavCmd(CommandType.MoveDown),     // → Model(4)
            NavCmd(CommandType.ActivateLink), // dispatch → enter Model picker
            NavCmd(CommandType.MoveDown),     // picker: tts-1(0) → tts-1-hd(1)
            NavCmd(CommandType.ActivateLink), // picker: pick tts-1-hd
            NavCmd(CommandType.GoBack),       // exit confirmation
        });
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => queue.Dequeue());

        var collection = Collection.Create("ITest");
        collection.AddItem("https://example.com/a", "A");
        var gcsConfig = new GcsConfiguration();

        var result = await PodcastConfirmationScreens.ShowConfirmationScreenAsync(
            _ctx,
            _options,
            collection,
            ttsService,
            gcsConfig,
            _settingsStore,
            gcsClient: null,
            cacheAnalysis: null,
            preflightBucketError: null,
            preflightFeedUrl: null,
            preflightFeedStatusNote: null,
            CancellationToken.None);

        result.Should().BeFalse();
        _settingsStore.Received(1).Set("OpenAiTtsModel", "tts-1-hd", Arg.Any<bool>());
    }

    /// <summary>
    /// workspace-sfhy regression: when TTS is configured the screen MUST default
    /// focus to the Generate row so the primary CTA is unmistakable on entry.
    /// We prove this by pressing Enter as the very first command — if the cursor
    /// were anywhere else (e.g. row 0 = TtsKey) Enter would open the key prompt
    /// instead of confirming generation, and ShowConfirmationScreenAsync would
    /// not return true.
    /// </summary>
    [Fact]
    public async Task ShowConfirmation_TtsConfigured_DefaultFocusIsGenerate()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.IsConfigured.Returns(true);

        var queue = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.ActivateLink), // Enter on default-focus row
        });
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => queue.Dequeue());

        var collection = Collection.Create("ITest");
        collection.AddItem("https://example.com/a", "A");
        var gcsConfig = new GcsConfiguration { BucketName = "some-bucket" };

        var result = await PodcastConfirmationScreens.ShowConfirmationScreenAsync(
            _ctx,
            _options,
            collection,
            ttsService,
            gcsConfig,
            _settingsStore,
            gcsClient: null,
            cacheAnalysis: null,
            preflightBucketError: null,
            preflightFeedUrl: null,
            preflightFeedStatusNote: null,
            CancellationToken.None);

        result.Should().BeTrue(
            "the Generate row must be the default focus when TTS is configured "
            + "— Enter on entry confirms generation rather than diving into a credential row");
    }

    /// <summary>
    /// workspace-sfhy guardrail: when TTS is NOT configured, the default focus
    /// must land on the TtsKey row so the user lands on the thing they need to
    /// fix first. We prove this by pressing Enter as the very first command:
    /// it must enter the OpenAI key prompt (PromptForInputAsync) rather than
    /// the (no-op-for-unconfigured) Generate path. The user then Esc's out and
    /// the screen returns false.
    /// </summary>
    [Fact]
    public async Task ShowConfirmation_TtsNotConfigured_DefaultFocusIsTtsKey()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.IsConfigured.Returns(false);

        var queue = new Queue<NavigationCommand>(new[]
        {
            NavCmd(CommandType.ActivateLink), // Enter on default-focus row
            NavCmd(CommandType.GoBack),       // exit the screen
        });
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => queue.Dequeue());

        // User cancels the key prompt (Esc → null).
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns((string?)null);

        var collection = Collection.Create("ITest");
        collection.AddItem("https://example.com/a", "A");
        var gcsConfig = new GcsConfiguration { BucketName = "some-bucket" };

        var result = await PodcastConfirmationScreens.ShowConfirmationScreenAsync(
            _ctx,
            _options,
            collection,
            ttsService,
            gcsConfig,
            _settingsStore,
            gcsClient: null,
            cacheAnalysis: null,
            preflightBucketError: null,
            preflightFeedUrl: null,
            preflightFeedStatusNote: null,
            CancellationToken.None);

        // The Enter keystroke must have dispatched into the TtsKey prompt — if
        // it had been on Generate, the key prompt would never have been called.
        await _inputHandler.Received().PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>());

        result.Should().BeFalse(
            "after cancelling the key prompt and pressing Esc, the screen returns "
            + "false (user did not confirm generation)");
    }

    #endregion
}
