// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Credentials;
using WireCopy.Domain.Enums;
using WireCopy.Domain.ValueObjects.Credentials;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class AutoLoginServiceTests
{
    private readonly ISiteCredentialRepository _credentialRepo;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly IPageAccessQueue _pageAccessQueue;
    private readonly IBrowserSession _browserSession;
    private readonly IPage _page;
    private readonly IBrowserContext _browserContext;
    private readonly ILocator _usernameLocator;
    private readonly ILocator _passwordLocator;
    private readonly ILocator _submitLocator;
    private readonly AutoLoginService _service;

    public AutoLoginServiceTests()
    {
        _credentialRepo = Substitute.For<ISiteCredentialRepository>();
        _encryptionService = Substitute.For<ICookieEncryptionService>();
        _pageAccessQueue = Substitute.For<IPageAccessQueue>();
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsBrowserAvailable.Returns(true);

        // Set up Playwright IPage mock
        _page = Substitute.For<IPage>();
        _browserContext = Substitute.For<IBrowserContext>();
        _page.Context.Returns(_browserContext);
        _page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(null));
        _page.GotoAsync(Arg.Any<string>()).Returns(Task.FromResult<IResponse?>(null));

        // Set up locator mocks
        _usernameLocator = Substitute.For<ILocator>();
        _passwordLocator = Substitute.For<ILocator>();
        _submitLocator = Substitute.For<ILocator>();
        _usernameLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
        _passwordLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
        _submitLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);

        // Default: AcquireAsync returns a lease with our mock page
        _pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(_ => new PageLease(_page, () => { }));

        // Set up service scope factory mock
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(ISiteCredentialRepository)).Returns(_credentialRepo);

        _service = new AutoLoginService(
            scopeFactory,
            _encryptionService,
            _pageAccessQueue,
            _browserSession,
            Substitute.For<ICookieManager>(),
            NullLogger<AutoLoginService>.Instance);
    }

    private static SiteCredential CreateCredential(
        string domain = "example.com",
        CredentialType type = CredentialType.FormLogin,
        string? usernameSelector = "#email",
        string? passwordSelector = "#password",
        string? submitSelector = "#submit",
        string? loginUrl = null,
        List<LoginStep>? loginSteps = null)
    {
        return SiteCredential.Create(
            domain,
            type,
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5, 6 },
            usernameSelector,
            passwordSelector,
            submitSelector,
            loginUrl,
            loginSteps);
    }

    private static SiteCredential CreateMultiStepCredential(
        string domain = "nytimes.com",
        string loginUrl = "https://myaccount.nytimes.com/auth/login")
    {
        var steps = new List<LoginStep>
        {
            new("#email", StepValueType.Username, "button[data-testid=submit-email]"),
            new("#password", StepValueType.Password, "button[type=submit]"),
        };

        return CreateCredential(
            domain: domain,
            loginUrl: loginUrl,
            usernameSelector: null,
            passwordSelector: null,
            submitSelector: null,
            loginSteps: steps);
    }

    private void SetupSuccessfulLogin(SiteCredential credential)
    {
        _credentialRepo.GetByDomainAsync(credential.Domain, Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user@example.com");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("s3cret");

        // Wire up Locator to return mock locators for form fields
        _page.Locator(credential.UsernameSelector!).Returns(_usernameLocator);
        _page.Locator(credential.PasswordSelector!).Returns(_passwordLocator);

        if (credential.SubmitSelector != null)
        {
            _page.Locator(credential.SubmitSelector).Returns(_submitLocator);
        }

        // Navigate away from login page after submission
        _page.Url.Returns("https://example.com/dashboard");
        _page.ContentAsync().Returns(Task.FromResult("<html><body>Dashboard</body></html>"));
        _browserContext.CookiesAsync(Arg.Any<IEnumerable<string>>()).Returns(Task.FromResult<IReadOnlyList<BrowserContextCookiesResult>>([]));
    }

    #region HasCredentialsAsync

    [Fact]
    public async Task HasCredentials_ReturnsTrue_WhenCredentialExists()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);

        var result = await _service.HasCredentialsAsync("example.com");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasCredentials_ReturnsFalse_WhenNoCredentialExists()
    {
        _credentialRepo.GetByDomainAsync("unknown.com", Arg.Any<CancellationToken>())
            .Returns((SiteCredential?)null);

        var result = await _service.HasCredentialsAsync("unknown.com");

        result.Should().BeFalse();
    }

    #endregion

    #region Login - No Credentials

    [Fact]
    public async Task Login_ReturnsFailed_WhenNoCredentialsExist()
    {
        _credentialRepo.GetByDomainAsync("unknown.com", Arg.Any<CancellationToken>())
            .Returns((SiteCredential?)null);

        var result = await _service.LoginAsync("unknown.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No credentials stored");
        result.ErrorMessage.Should().Contain("unknown.com");
    }

    #endregion

    #region Login - Decrypt Failure

    [Fact]
    public async Task Login_ReturnsFailed_WhenDecryptionThrows()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(Arg.Any<byte[]>())
            .Throws(new InvalidOperationException("Decryption failed"));

        var result = await _service.LoginAsync("example.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to decrypt");
    }

    #endregion

    #region Login - Successful Form Login

    [Fact]
    public async Task Login_ReturnsSuccess_WhenFormLoginSucceeds()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        var result = await _service.LoginAsync("example.com");

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ManualLoginRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Login_FillsUsernameAndPassword_OnFormLogin()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        // Verify FillAsync was called with correct values
        await _usernameLocator.Received().FillAsync("user@example.com");
        await _passwordLocator.Received().FillAsync("s3cret");
    }

    [Fact]
    public async Task Login_ClicksSubmitButton_WhenSelectorConfigured()
    {
        var credential = CreateCredential(submitSelector: "#login-btn");
        _credentialRepo.GetByDomainAsync(credential.Domain, Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user@example.com");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("s3cret");

        var loginBtnLocator = Substitute.For<ILocator>();
        loginBtnLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
        _page.Locator("#email").Returns(_usernameLocator);
        _page.Locator("#password").Returns(_passwordLocator);
        _page.Locator("#login-btn").Returns(loginBtnLocator);
        _page.Url.Returns("https://example.com/dashboard");
        _browserContext.CookiesAsync(Arg.Any<IEnumerable<string>>()).Returns(Task.FromResult<IReadOnlyList<BrowserContextCookiesResult>>([]));

        await _service.LoginAsync("example.com");

        await loginBtnLocator.Received().ClickAsync();
    }

    [Fact]
    public async Task Login_NavigatesToLoginUrl_WhenConfigured()
    {
        var credential = CreateCredential(loginUrl: "https://auth.example.com/signin");
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _page.Received().GotoAsync("https://auth.example.com/signin");
    }

    [Fact]
    public async Task Login_NavigatesToDefaultLoginUrl_WhenNotConfigured()
    {
        var credential = CreateCredential(loginUrl: null);
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _page.Received().GotoAsync("https://example.com/login");
    }

    #endregion

    #region Login - Missing Selectors (Manual Fallback)

    [Fact]
    public async Task Login_RequiresManualLogin_WhenUsernameSelectorMissing()
    {
        var credential = CreateCredential(usernameSelector: null);
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        var result = await _service.LoginAsync("example.com");

        result.ManualLoginRequired.Should().BeTrue();
        result.ErrorMessage.Should().Contain("selectors not configured");
    }

    [Fact]
    public async Task Login_RequiresManualLogin_WhenPasswordSelectorMissing()
    {
        var credential = CreateCredential(passwordSelector: null);
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        var result = await _service.LoginAsync("example.com");

        result.ManualLoginRequired.Should().BeTrue();
        result.ErrorMessage.Should().Contain("selectors not configured");
    }

    [Fact]
    public async Task Login_RestoresWindow_WhenSelectorsMissing()
    {
        var credential = CreateCredential(usernameSelector: null);
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        await _service.LoginAsync("example.com");

        await _browserSession.Received(1).RestoreWindowAsync();
    }

    #endregion

    #region Login - Element Not Found (Manual Fallback)

    [Fact]
    public async Task Login_RequiresManualLogin_WhenElementNotFound()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        // Playwright throws PlaywrightException when element not found
        _page.GotoAsync(Arg.Any<string>())
            .Returns<IResponse?>(_ => throw new PlaywrightException("Element not found"));

        var result = await _service.LoginAsync("example.com");

        // The PlaywrightException with "Element" is caught and triggers manual login
        result.ManualLoginRequired.Should().BeTrue();
        result.ErrorMessage.Should().Contain("elements not found");
    }

    [Fact]
    public async Task Login_RestoresWindow_WhenElementNotFound()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        _page.GotoAsync(Arg.Any<string>())
            .Returns<IResponse?>(_ => throw new PlaywrightException("Element not found"));

        await _service.LoginAsync("example.com");

        await _browserSession.Received(1).RestoreWindowAsync();
    }

    #endregion

    #region Login - Browser Error

    [Fact]
    public async Task Login_ReturnsFailed_OnPlaywrightException()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        _page.GotoAsync(Arg.Any<string>())
            .Returns<IResponse?>(_ => throw new PlaywrightException("Session crashed"));

        var result = await _service.LoginAsync("example.com");

        result.Success.Should().BeFalse();
        result.ManualLoginRequired.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser error");
    }

    #endregion

    #region Login - Background Priority

    [Fact]
    public async Task Login_UsesBackgroundPriority()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _pageAccessQueue.Received(1).AcquireAsync(
            PageAccessPriority.Background,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_NeverUsesForegroundPriority()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _pageAccessQueue.DidNotReceive().AcquireAsync(
            PageAccessPriority.Foreground,
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Login - Lease Disposal

    [Fact]
    public async Task Login_DisposesLease_AfterSuccess()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        var leaseDisposed = false;
        var lease = new PageLease(_page, () => leaseDisposed = true);
        _pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(lease);

        await _service.LoginAsync("example.com");

        leaseDisposed.Should().BeTrue("lease should be disposed after login completes");
    }

    [Fact]
    public async Task Login_DisposesLease_AfterFailure()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        _page.GotoAsync(Arg.Any<string>())
            .Returns<IResponse?>(_ => throw new PlaywrightException("Crash"));

        var leaseDisposed = false;
        var lease = new PageLease(_page, () => leaseDisposed = true);
        _pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(lease);

        await _service.LoginAsync("example.com");

        leaseDisposed.Should().BeTrue("lease should be disposed even after failure");
    }

    #endregion

    #region Login - Unsupported Credential Type

    [Fact]
    public async Task Login_ReturnsFailed_ForBasicAuthCredentialType()
    {
        var credential = CreateCredential(type: CredentialType.BasicAuth);
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        var result = await _service.LoginAsync("example.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unsupported credential type");
    }

    #endregion

    #region Login - Multi-Step Login

    [Fact]
    public async Task Login_ReturnsSuccess_WhenMultiStepLoginCompletes()
    {
        var credential = CreateMultiStepCredential();
        SetupMultiStepLogin(credential);

        var result = await _service.LoginAsync("nytimes.com");

        result.Success.Should().BeTrue();
    }

    private void SetupMultiStepLogin(SiteCredential credential)
    {
        _credentialRepo.GetByDomainAsync(credential.Domain, Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user@example.com");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("s3cret");

        var emailLocator = Substitute.For<ILocator>();
        var passwordLocator = Substitute.For<ILocator>();
        var continueLocator = Substitute.For<ILocator>();
        var submitLocator = Substitute.For<ILocator>();
        emailLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
        passwordLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
        continueLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
        submitLocator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);

        _page.Locator("#email").Returns(emailLocator);
        _page.Locator("#password").Returns(passwordLocator);
        _page.Locator("button[data-testid=submit-email]").Returns(continueLocator);
        _page.Locator("button[type=submit]").Returns(submitLocator);

        _page.Url.Returns("https://www.nytimes.com");
        _page.ContentAsync().Returns(Task.FromResult("<html><body>NYT Home</body></html>"));
        _browserContext.CookiesAsync(Arg.Any<IEnumerable<string>>()).Returns(Task.FromResult<IReadOnlyList<BrowserContextCookiesResult>>([]));
    }

    #endregion

    #region Login - No Credentials Does Not Acquire Driver

    [Fact]
    public async Task Login_DoesNotAcquireDriver_WhenNoCredentials()
    {
        _credentialRepo.GetByDomainAsync("unknown.com", Arg.Any<CancellationToken>())
            .Returns((SiteCredential?)null);

        await _service.LoginAsync("unknown.com");

        await _pageAccessQueue.DidNotReceive().AcquireAsync(
            Arg.Any<PageAccessPriority>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_DoesNotAcquireDriver_WhenDecryptionFails()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(Arg.Any<byte[]>())
            .Throws(new InvalidOperationException("Decryption failed"));

        await _service.LoginAsync("example.com");

        await _pageAccessQueue.DidNotReceive().AcquireAsync(
            Arg.Any<PageAccessPriority>(),
            Arg.Any<CancellationToken>());
    }

    #endregion
}
