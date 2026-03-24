// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

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

    #region :set apikey

    [Fact]
    public async Task SetApiKey_ValidKey_PersistsEncrypted()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), isSecret: true)
            .Returns(Task.FromResult<string?>("sk-test-key-12345"));

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(new TtsValidationResult { IsValid = true });

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set apikey", _options, CancellationToken.None);

        _settingsStore.Received(1).Set("OpenAiApiKey", "sk-test-key-12345", encrypt: true);
        _navigationService.CurrentContext.StatusMessage.Should().Contain("saved");
    }

    [Fact]
    public async Task SetApiKey_InvalidKey_DoesNotPersist()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), isSecret: true)
            .Returns(Task.FromResult<string?>("bad-key"));

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(new TtsValidationResult { IsValid = false, ErrorMessage = "Invalid API key" });

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set apikey", _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        _ttsService.Received(1).SetApiKeyOverride(string.Empty);
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Invalid API key");
    }

    [Fact]
    public async Task SetApiKey_EmptyInput_DoesNothing()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), isSecret: true)
            .Returns(Task.FromResult<string?>(""));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set apikey", _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task SetApiKey_WhitespaceKey_IsTrimmedBeforeValidation()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), isSecret: true)
            .Returns(Task.FromResult<string?>("  sk-trimmed  "));

        _ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(new TtsValidationResult { IsValid = true });

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set apikey", _options, CancellationToken.None);

        _ttsService.Received(1).SetApiKeyOverride("sk-trimmed");
        _settingsStore.Received(1).Set("OpenAiApiKey", "sk-trimmed", encrypt: true);
    }

    [Fact]
    public async Task SetApiKey_UsesSecretInput()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), isSecret: true)
            .Returns(Task.FromResult<string?>(null));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set apikey", _options, CancellationToken.None);

        await _inputHandler.Received(1).PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(), isSecret: true);
    }

    #endregion

    #region :set bucket

    [Fact]
    public async Task SetBucket_ValidName_PersistsAndUpdatesConfig()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("my-podcast-bucket"));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set bucket", _options, CancellationToken.None);

        _settingsStore.Received(1).Set("GcsBucketName", "my-podcast-bucket");
        _gcsConfig.BucketName.Should().Be("my-podcast-bucket");
        _navigationService.CurrentContext.StatusMessage.Should().Contain("my-podcast-bucket");
    }

    [Fact]
    public async Task SetBucket_InvalidName_ShowsError()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("AB"));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set bucket", _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Invalid");
    }

    [Fact]
    public async Task SetBucket_EmptyInput_DoesNothing()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>(""));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set bucket", _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        _gcsConfig.BucketName.Should().BeNull();
    }

    [Fact]
    public async Task SetBucket_NameWithUpperCase_IsRejected()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("My-Bucket"));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set bucket", _options, CancellationToken.None);

        _settingsStore.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Invalid");
    }

    [Fact]
    public async Task SetBucket_WhitespaceName_IsTrimmedBeforeValidation()
    {
        _inputHandler.PromptForInputAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("  valid-bucket  "));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "set bucket", _options, CancellationToken.None);

        _settingsStore.Received(1).Set("GcsBucketName", "valid-bucket");
        _gcsConfig.BucketName.Should().Be("valid-bucket");
    }

    #endregion

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
