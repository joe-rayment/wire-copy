// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for :set apikey and :set bucket commands.
/// </summary>
[Trait("Category", "Unit")]
public class SetCommandTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly IInputHandler _inputHandler;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ITtsService _ttsService;
    private readonly GcsConfiguration _gcsConfig;
    private bool _renderCalled;

    public SetCommandTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        _navigationService.NavigateTo(page);

        _inputHandler = Substitute.For<IInputHandler>();
        _settingsStore = Substitute.For<IUserSettingsStore>();
        _ttsService = Substitute.For<ITtsService>();
        _gcsConfig = new GcsConfiguration();

        var gcsOptions = Substitute.For<IOptions<GcsConfiguration>>();
        gcsOptions.Value.Returns(_gcsConfig);

        // Set up service scope factory to return our mocks
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(_settingsStore);
        serviceProvider.GetService(typeof(ITtsService)).Returns(_ttsService);
        serviceProvider.GetService(typeof(IOptions<GcsConfiguration>)).Returns(gcsOptions);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 80,
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = _inputHandler,
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(_navigationService, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
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

    #region :set (no subcommand / invalid subcommand)

    [Fact]
    public async Task Set_NoSubcommand_ShowsUsageMessage()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Usage");
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Set_InvalidSubcommand_ShowsUsageMessage()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set foo", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Usage");
    }

    #endregion

    // NOTE: SetApiKey tests removed — they mocked IInputHandler.PromptForInputAsync but
    // the code path goes through FormField.PromptAsync which calls Console.ReadKey directly,
    // so the mocks were never reached and the tests hung indefinitely in CI.
    // To properly test :set apikey, FormField needs to accept an IInputHandler dependency.

    // NOTE: :set bucket tests were removed in workspace-dwgl. The bucket flow
    // now uses an inline FormField (which calls Console.ReadKey directly and
    // can't be mocked here) and runs a real GCP probe before persisting. The
    // FormField validator is covered by
    // WireCopy.Tests.Configuration.GcsBucketNameValidationTests; the probe
    // wrapper by WireCopy.Tests.Podcast.GcsBucketProbeTests; live behaviour
    // is exercised via scripts/termtest.py.

    #region :clear apikey

    [Fact]
    public async Task ClearApiKey_RemovesFromStore()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "clear apikey", _options, CancellationToken.None);

        _settingsStore.Received(1).Remove("OpenAiApiKey");
        _ttsService.Received(1).SetApiKeyOverride(string.Empty);
        _navigationService.CurrentContext.StatusMessage.Should().Contain("cleared");
    }

    #endregion

    #region :clear bucket

    [Fact]
    public async Task ClearBucket_RemovesFromStoreAndClearsConfig()
    {
        _gcsConfig.BucketName = "old-bucket";

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "clear bucket", _options, CancellationToken.None);

        _settingsStore.Received(1).Remove("GcsBucketName");
        _gcsConfig.BucketName.Should().BeNull();
        _navigationService.CurrentContext.StatusMessage.Should().Contain("cleared");
    }

    #endregion

    #region :clear (no subcommand - delegates to collection clear)

    [Fact]
    public async Task Clear_NoSubcommand_DoesNotTouchSettings()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "clear", _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Remove(Arg.Any<string>());
        _renderCalled.Should().BeTrue();
    }

    #endregion
}
