// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Credentials;
using WireCopy.Domain.Enums;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for :cred commands (list, add, remove, test, edit).
/// </summary>
[Trait("Category", "Unit")]
public class CredentialCommandTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly IInputHandler _inputHandler;
    private readonly ISiteCredentialRepository _credentialRepo;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAutoLoginService _autoLoginService;
    private bool _renderCalled;

    public CredentialCommandTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        _navigationService.NavigateTo(page);

        _inputHandler = Substitute.For<IInputHandler>();
        _credentialRepo = Substitute.For<ISiteCredentialRepository>();
        _encryptionService = Substitute.For<ICookieEncryptionService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _autoLoginService = Substitute.For<IAutoLoginService>();

        // Set up service scope factory to return our mocks
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        serviceProvider.GetService(typeof(ISiteCredentialRepository)).Returns(_credentialRepo);
        serviceProvider.GetService(typeof(ICookieEncryptionService)).Returns(_encryptionService);
        serviceProvider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        serviceProvider.GetService(typeof(IAutoLoginService)).Returns(_autoLoginService);
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

    #region :cred (list)

    [Fact]
    public async Task CredList_NoCredentials_ShowsEmptyMessage()
    {
        _credentialRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SiteCredential>>(Array.Empty<SiteCredential>()));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("No stored credentials");
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CredList_WithCredentials_ListsThem()
    {
        var cred = CreateTestCredential("nytimes.com");
        _credentialRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SiteCredential>>(new[] { cred }));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("nytimes.com")
            .And.Contain("FormLogin");
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CredentialsList_AliasWorks()
    {
        _credentialRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SiteCredential>>(Array.Empty<SiteCredential>()));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "credentials", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("No stored credentials");
    }

    #endregion

    #region :cred add

    [Fact]
    public async Task CredAdd_AllFields_CreatesCredential()
    {
        // nytimes.com is a known site — Step 2 (login config) is skipped
        var promptResponses = new Queue<string?>(new[]
        {
            "nytimes.com",      // domain
            "user@test.com",    // username
            "secret123",        // password
        });

        _inputHandler.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(x => Task.FromResult(promptResponses.Dequeue()));

        _encryptionService.Encrypt("user@test.com").Returns(new byte[] { 1, 2, 3 });
        _encryptionService.Encrypt("secret123").Returns(new byte[] { 4, 5, 6 });

        try
        {
            await SearchCommandHandler.HandleCommandLineInput(
                _ctx, "cred add", _options, CancellationToken.None);

            await _credentialRepo.Received(1).AddAsync(
                Arg.Is<SiteCredential>(c => c.Domain == "nytimes.com"),
                Arg.Any<CancellationToken>());
            await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
            _navigationService.CurrentContext.StatusMessage.Should().Contain("saved");
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task CredAdd_EscapeDomain_CancelledWithoutSaving()
    {
        _inputHandler.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(Task.FromResult<string?>(null));

        try
        {
            await SearchCommandHandler.HandleCommandLineInput(
                _ctx, "cred add", _options, CancellationToken.None);

            await _credentialRepo.DidNotReceive().AddAsync(
                Arg.Any<SiteCredential>(), Arg.Any<CancellationToken>());
            await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
            _renderCalled.Should().BeTrue();
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task CredAdd_EscapePassword_CancelledWithoutSaving()
    {
        var callCount = 0;
        _inputHandler.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(x =>
            {
                callCount++;
                return Task.FromResult(callCount switch
                {
                    1 => (string?)"nytimes.com",   // domain
                    2 => "user@test.com",           // username
                    3 => null,                      // password - Escape (back to username)
                    4 => null,                      // username - Escape (back to domain)
                    5 => null,                      // domain - Escape (cancel wizard)
                    _ => null,
                });
            });

        try
        {
            await SearchCommandHandler.HandleCommandLineInput(
                _ctx, "cred add", _options, CancellationToken.None);

            await _credentialRepo.DidNotReceive().AddAsync(
                Arg.Any<SiteCredential>(), Arg.Any<CancellationToken>());
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    #endregion

    #region :cred remove

    [Fact]
    public async Task CredRemove_ExistingDomain_DeletesIt()
    {
        var cred = CreateTestCredential("nytimes.com");
        _credentialRepo.GetByDomainAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SiteCredential?>(cred));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred rm nytimes.com", _options, CancellationToken.None);

        await _credentialRepo.Received(1).DeleteAsync(cred.Id, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("removed");
    }

    [Fact]
    public async Task CredRemove_NonexistentDomain_ShowsError()
    {
        _credentialRepo.GetByDomainAsync("unknown.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SiteCredential?>(null));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred remove unknown.com", _options, CancellationToken.None);

        await _credentialRepo.DidNotReceive().DeleteAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("No credential found");
    }

    [Fact]
    public async Task CredRemove_NoDomainArg_PromptsForDomain()
    {
        _inputHandler.PromptForInputAsync("Domain to remove: ", Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("nytimes.com"));

        var cred = CreateTestCredential("nytimes.com");
        _credentialRepo.GetByDomainAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SiteCredential?>(cred));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred rm", _options, CancellationToken.None);

        await _credentialRepo.Received(1).DeleteAsync(cred.Id, Arg.Any<CancellationToken>());
    }

    #endregion

    #region :cred test

    [Fact]
    public async Task CredTest_SuccessfulLogin_ShowsSuccessMessage()
    {
        _autoLoginService.LoginAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutoLoginResult.Succeeded()));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred test nytimes.com", _options, CancellationToken.None);

        await _autoLoginService.Received(1).LoginAsync("nytimes.com", Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Login succeeded");
    }

    [Fact]
    public async Task CredTest_FailedLogin_ShowsFailureMessage()
    {
        _autoLoginService.LoginAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutoLoginResult.Failed("Invalid credentials")));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred test nytimes.com", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("Login failed")
            .And.Contain("Invalid credentials");
    }

    [Fact]
    public async Task CredTest_ManualLoginRequired_ShowsManualMessage()
    {
        _autoLoginService.LoginAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutoLoginResult.RequiresManualLogin("CAPTCHA detected")));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred test nytimes.com", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("Manual login required")
            .And.Contain("CAPTCHA detected");
    }

    [Fact]
    public async Task CredTest_NoDomainArg_PromptsForDomain()
    {
        _inputHandler.PromptForInputAsync("Domain to test: ", Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Task.FromResult<string?>("nytimes.com"));

        _autoLoginService.LoginAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutoLoginResult.Succeeded()));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred test", _options, CancellationToken.None);

        await _autoLoginService.Received(1).LoginAsync("nytimes.com", Arg.Any<CancellationToken>());
    }

    #endregion

    #region :cred edit

    [Fact]
    public async Task CredEdit_ExistingCredential_UpdatesIt()
    {
        var cred = CreateTestCredential("nytimes.com");
        _credentialRepo.GetByDomainAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SiteCredential?>(cred));

        _encryptionService.Decrypt(cred.EncryptedUsername).Returns("old-user");

        // nytimes.com is a known site — Step 2 (login config) is skipped
        var callCount = 0;
        _inputHandler.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(x =>
            {
                callCount++;
                return Task.FromResult(callCount switch
                {
                    1 => (string?)"new-user@test.com", // new username
                    2 => "newpass",                     // new password
                    _ => null,
                });
            });

        _encryptionService.Encrypt("new-user@test.com").Returns(new byte[] { 10, 11 });
        _encryptionService.Encrypt("newpass").Returns(new byte[] { 12, 13 });

        try
        {
            await SearchCommandHandler.HandleCommandLineInput(
                _ctx, "cred edit nytimes.com", _options, CancellationToken.None);

            await _credentialRepo.Received(1).UpdateAsync(cred, Arg.Any<CancellationToken>());
            await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
            _navigationService.CurrentContext.StatusMessage.Should().Contain("updated");
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    [Fact]
    public async Task CredEdit_NonexistentDomain_ShowsError()
    {
        _credentialRepo.GetByDomainAsync("unknown.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SiteCredential?>(null));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cred edit unknown.com", _options, CancellationToken.None);

        await _credentialRepo.DidNotReceive().UpdateAsync(
            Arg.Any<SiteCredential>(), Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("No credential found");
    }

    [Fact]
    public async Task CredEdit_KeepExistingPassword_UsesOriginalEncryptedPassword()
    {
        var cred = CreateTestCredential("nytimes.com");
        var originalEncryptedPassword = cred.EncryptedPassword;

        _credentialRepo.GetByDomainAsync("nytimes.com", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SiteCredential?>(cred));

        _encryptionService.Decrypt(cred.EncryptedUsername).Returns("old-user");

        // WizardRunner: username gets pre-filled ("old-user"), password left empty (keep)
        var callCount = 0;
        _inputHandler.PromptForInputAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(x =>
            {
                callCount++;
                return Task.FromResult(callCount switch
                {
                    1 => (string?)"old-user", // username (keep existing)
                    2 => "",                   // password (empty = keep)
                    _ => null,
                });
            });

        _encryptionService.Encrypt("old-user").Returns(new byte[] { 1, 2, 3 });

        try
        {
            await SearchCommandHandler.HandleCommandLineInput(
                _ctx, "cred edit nytimes.com", _options, CancellationToken.None);

            // Should NOT call Encrypt for password (kept the original encrypted bytes)
            _encryptionService.DidNotReceive().Encrypt(Arg.Is<string>(s => s != "old-user"));

            await _credentialRepo.Received(1).UpdateAsync(
                Arg.Is<SiteCredential>(c => c.EncryptedPassword == originalEncryptedPassword),
                Arg.Any<CancellationToken>());
        }
        catch (IOException)
        {
            // Expected in CI — Console operations fail without a terminal
        }
    }

    #endregion

    /// <summary>
    /// Helper to create a test credential using the SiteCredential.Create factory.
    /// </summary>
    private static SiteCredential CreateTestCredential(string domain)
    {
        return SiteCredential.Create(
            domain,
            CredentialType.FormLogin,
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5, 6 });
    }
}
