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
/// Tests for the unified <c>:config</c> Setup screen (workspace-fn1u).
///
/// Covers:
/// * <see cref="SettingsCommandHandler.IsFirstRun(IUserSettingsStore)"/>
///   — auto-landing condition for fresh installs.
/// * <see cref="SettingsCommandHandler.DispatchRowAsync"/> — every row's Enter
///   dispatch invokes the right handler and persists via
///   <see cref="IUserSettingsStore"/> with the canonical settings key.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsCommandHandlerTests
{
    private readonly IInputHandler _inputHandler;
    private readonly IUserSettingsStore _settingsStore;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;

    public SettingsCommandHandlerTests()
    {
        _inputHandler = Substitute.For<IInputHandler>();
        _settingsStore = Substitute.For<IUserSettingsStore>();

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(navLogger);

        var ttsOptions = Substitute.For<IOptions<OpenAiTtsConfiguration>>();
        ttsOptions.Value.Returns(new OpenAiTtsConfiguration());
        var hierarchyOptions = Substitute.For<IOptions<OpenAiHierarchyConfiguration>>();
        hierarchyOptions.Value.Returns(new OpenAiHierarchyConfiguration());
        var podcastOptions = Substitute.For<IOptions<PodcastConfiguration>>();
        podcastOptions.Value.Returns(new PodcastConfiguration());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(_settingsStore);
        serviceProvider.GetService(typeof(IOptions<OpenAiTtsConfiguration>)).Returns(ttsOptions);
        serviceProvider.GetService(typeof(IOptions<OpenAiHierarchyConfiguration>)).Returns(hierarchyOptions);
        serviceProvider.GetService(typeof(IOptions<PodcastConfiguration>)).Returns(podcastOptions);
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
    }

    #region First-run detection

    [Fact]
    public void IsFirstRun_AllSettingsEmpty_ReturnsTrue()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(Arg.Any<string>()).Returns((string?)null);

        SettingsCommandHandler.IsFirstRun(store).Should().BeTrue(
            "fresh install with zero credentials must trigger the Setup landing");
    }

    [Fact]
    public void IsFirstRun_OpenAiKeySet_ReturnsFalse()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(SettingsCommandHandler.KeyOpenAiApiKey).Returns("sk-abc");

        SettingsCommandHandler.IsFirstRun(store).Should().BeFalse();
    }

    [Fact]
    public void IsFirstRun_BucketSet_ReturnsFalse()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(SettingsCommandHandler.KeyGcsBucketName).Returns("my-bucket");

        SettingsCommandHandler.IsFirstRun(store).Should().BeFalse();
    }

    [Fact]
    public void IsFirstRun_GcsKeyPathSet_ReturnsFalse()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(SettingsCommandHandler.KeyGcsServiceAccountKeyPath).Returns("/tmp/key.json");

        SettingsCommandHandler.IsFirstRun(store).Should().BeFalse();
    }

    [Fact]
    public void IsFirstRun_WhitespaceValuesTreatedAsEmpty()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(Arg.Any<string>()).Returns("   ");

        SettingsCommandHandler.IsFirstRun(store).Should().BeTrue(
            "whitespace-only values must not count as configured");
    }

    #endregion

    #region Incomplete-setup detection (workspace-9qzh)

    [Fact]
    public void HasIncompleteSetup_AllSettingsEmpty_ReturnsTrue()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(Arg.Any<string>()).Returns((string?)null);

        SettingsCommandHandler.HasIncompleteSetup(store).Should().BeTrue(
            "zero credentials → setup hint must show");
    }

    [Fact]
    public void HasIncompleteSetup_OnlyOneCredentialSet_ReturnsTrue()
    {
        // Partial-setup case from the user's bug: OpenAI key configured but
        // GCS still missing. The launcher hint must remain visible to point
        // them at Setup.
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(SettingsCommandHandler.KeyOpenAiApiKey).Returns("sk-abc");

        SettingsCommandHandler.HasIncompleteSetup(store).Should().BeTrue(
            "any unset credential keeps the hint visible");
    }

    [Fact]
    public void HasIncompleteSetup_AllThreeCredentialsSet_ReturnsFalse()
    {
        // workspace-65sw: AI Curated layout now uses the OpenAI key (one
        // credential covers TTS + hierarchy), so the Anthropic-key requirement
        // dropped out of the predicate.
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(SettingsCommandHandler.KeyOpenAiApiKey).Returns("sk-abc");
        store.Get(SettingsCommandHandler.KeyGcsBucketName).Returns("my-bucket");
        store.Get(SettingsCommandHandler.KeyGcsServiceAccountKeyPath).Returns("/tmp/key.json");

        SettingsCommandHandler.HasIncompleteSetup(store).Should().BeFalse(
            "fully configured users see no hint; S still works to open Setup via the keybinding");
    }

    [Fact]
    public void HasIncompleteSetup_WhitespaceValuesTreatedAsEmpty()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(Arg.Any<string>()).Returns("   ");

        SettingsCommandHandler.HasIncompleteSetup(store).Should().BeTrue(
            "whitespace-only values must not count as configured");
    }

    #endregion

    #region Row dispatch

    [Fact]
    public async Task DispatchRow_OutputFolder_PromptsAndPersists()
    {
        // OutputFolder row dispatch should drive PromptForInputAsync and
        // persist via the canonical "PodcastOutputFolder" settings key.
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("/tmp/setup-out");

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.OutputFolder, CancellationToken.None);

        _settingsStore.Received(1).Set(
            SettingsCommandHandler.KeyPodcastOutputFolder,
            "/tmp/setup-out",
            Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_Voice_PicksAndPersists()
    {
        _settingsStore.Get(SettingsCommandHandler.KeyOpenAiTtsVoice).Returns("nova");
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.ActivateLink });

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.Voice, CancellationToken.None);

        // Default cursor lands on current voice ("nova"), Enter persists it.
        _settingsStore.Received(1).Set(
            SettingsCommandHandler.KeyOpenAiTtsVoice, "nova", Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_Model_PicksAndPersists()
    {
        _settingsStore.Get(SettingsCommandHandler.KeyOpenAiTtsModel).Returns("tts-1");
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.ActivateLink });

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.Model, CancellationToken.None);

        _settingsStore.Received(1).Set(
            SettingsCommandHandler.KeyOpenAiTtsModel, "tts-1", Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_TtsInstructions_PromptsAndPersists()
    {
        // TtsInstructions row dispatch should drive PromptForInputAsync and
        // persist via the canonical "OpenAiTtsInstructions" settings key.
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("Speak softly");

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.TtsInstructions, CancellationToken.None);

        _settingsStore.Received(1).Set(
            SettingsCommandHandler.KeyOpenAiTtsInstructions,
            "Speak softly",
            Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_TtsInstructions_ResetClearsOverride()
    {
        // "reset" reverts to the bound config default.
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("reset");

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.TtsInstructions, CancellationToken.None);

        _settingsStore.Received(1).Remove(SettingsCommandHandler.KeyOpenAiTtsInstructions);
    }

    [Fact]
    public async Task DispatchRow_TtsInstructions_NonePersistsEmptyString()
    {
        // "none" persists an empty override — distinct from "reset" — so the
        // request omits the instructions field entirely until the user picks
        // a new value.
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("none");

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.TtsInstructions, CancellationToken.None);

        _settingsStore.Received(1).Set(
            SettingsCommandHandler.KeyOpenAiTtsInstructions,
            string.Empty,
            Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_AutoPurgeHours_PersistsParsedValue()
    {
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("48");

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.AutoPurgeHours, CancellationToken.None);

        _settingsStore.Received(1).Set(
            SettingsCommandHandler.KeyOutputRetentionHours, "48", Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_AutoPurgeHours_RejectsNonInteger()
    {
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("abc");

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.AutoPurgeHours, CancellationToken.None);

        _settingsStore.DidNotReceive().Set(
            SettingsCommandHandler.KeyOutputRetentionHours,
            Arg.Any<string>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_AutoPurgeHours_ResetClearsOverride()
    {
        _inputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("reset");

        await SettingsCommandHandler.DispatchRowAsync(
            _ctx, _options, SettingsCommandHandler.SetupRow.AutoPurgeHours, CancellationToken.None);

        _settingsStore.Received(1).Remove(SettingsCommandHandler.KeyOutputRetentionHours);
    }

    #endregion

    #region Setup screen rendering and Esc

    [Fact]
    public async Task HandleConfigScreen_EscReturnsToCaller_NoPersistence()
    {
        // Esc on the Setup screen returns to the launcher without writing anything.
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.GoBack });

        await SettingsCommandHandler.HandleConfigScreen(
            _ctx, _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Set(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        _settingsStore.DidNotReceive().Remove(Arg.Any<string>());
    }

    #endregion
}
