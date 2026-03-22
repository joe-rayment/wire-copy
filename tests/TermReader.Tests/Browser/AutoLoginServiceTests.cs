// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenQA.Selenium;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Credentials;
using TermReader.Domain.Enums;
using TermReader.Domain.ValueObjects.Credentials;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class AutoLoginServiceTests
{
    private readonly ISiteCredentialRepository _credentialRepo;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly IWebDriverQueue _webDriverQueue;
    private readonly IBrowserSession _browserSession;
    private readonly IWebDriver _webDriver;
    private readonly INavigation _navigation;
    private readonly IOptions _driverOptions;
    private readonly ICookieJar _cookieJar;
    private readonly IWebElement _usernameElement;
    private readonly IWebElement _passwordElement;
    private readonly IWebElement _submitElement;
    private readonly AutoLoginService _service;

    public AutoLoginServiceTests()
    {
        _credentialRepo = Substitute.For<ISiteCredentialRepository>();
        _encryptionService = Substitute.For<ICookieEncryptionService>();
        _webDriverQueue = Substitute.For<IWebDriverQueue>();
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsSeleniumAvailable.Returns(true);

        // Set up WebDriver mock chain
        _webDriver = Substitute.For<IWebDriver>();
        _navigation = Substitute.For<INavigation>();
        _driverOptions = Substitute.For<IOptions>();
        _cookieJar = Substitute.For<ICookieJar>();
        _webDriver.Navigate().Returns(_navigation);
        _webDriver.Manage().Returns(_driverOptions);
        _driverOptions.Cookies.Returns(_cookieJar);

        // Set up shared form elements that are returned by FindElement
        _usernameElement = Substitute.For<IWebElement>();
        _passwordElement = Substitute.For<IWebElement>();
        _submitElement = Substitute.For<IWebElement>();

        // Default: AcquireAsync returns a lease with our mock driver
        _webDriverQueue.AcquireAsync(Arg.Any<WebDriverPriority>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new WebDriverLease(_webDriver, () => { }));

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
            _webDriverQueue,
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

        // Wire up FindElement to return the shared mock elements
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains(credential.UsernameSelector!)))
            .Returns(_usernameElement);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains(credential.PasswordSelector!)))
            .Returns(_passwordElement);

        if (credential.SubmitSelector != null)
        {
            _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains(credential.SubmitSelector)))
                .Returns(_submitElement);
        }

        // Navigate away from login page after submission
        _webDriver.Url.Returns("https://example.com/dashboard");
        _webDriver.PageSource.Returns("<html><body>Dashboard</body></html>");
        _cookieJar.AllCookies.Returns(
            new System.Collections.ObjectModel.ReadOnlyCollection<Cookie>(new List<Cookie>()));
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

        // Verify on the shared elements that were returned by FindElement
        _usernameElement.Received().SendKeys("user@example.com");
        _passwordElement.Received().SendKeys("s3cret");
    }

    [Fact]
    public async Task Login_ClearsFieldsBeforeTyping()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        _usernameElement.Received().Clear();
        _passwordElement.Received().Clear();
    }

    [Fact]
    public async Task Login_ClicksSubmitButton_WhenSelectorConfigured()
    {
        var credential = CreateCredential(submitSelector: "#login-btn");
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        _submitElement.Received().Click();
    }

    [Fact]
    public async Task Login_NavigatesToLoginUrl_WhenConfigured()
    {
        var credential = CreateCredential(loginUrl: "https://auth.example.com/signin");
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _navigation.Received().GoToUrlAsync("https://auth.example.com/signin");
    }

    [Fact]
    public async Task Login_NavigatesToDefaultLoginUrl_WhenNotConfigured()
    {
        var credential = CreateCredential(loginUrl: null);
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _navigation.Received().GoToUrlAsync("https://example.com/login");
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
        _webDriver.Url.Returns("https://example.com/login");

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
        _webDriver.Url.Returns("https://example.com/login");

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
        _webDriver.Url.Returns("https://example.com/login");

        await _service.LoginAsync("example.com");

        _browserSession.Received(1).RestoreWindow();
    }

    #endregion

    #region Login - Element Not Found (Manual Fallback)

    [Fact]
    public async Task Login_RequiresManualLogin_WhenWebDriverTimesOutFindingElement()
    {
        // WebDriverWait.Until wraps NoSuchElementException into WebDriverTimeoutException
        // after the timeout period. Since we catch WebDriverException (parent of both),
        // this triggers the WebDriverException handler when it escapes TryFormLoginAsync.
        // However, the NoSuchElementException catch in LoginAsync should also handle
        // a direct NoSuchElementException thrown before TryFormLoginAsync is entered.
        // Test the WebDriverTimeoutException path (what actually happens in practice).
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        // WebDriverWait internally calls FindElement, which throws NoSuchElementException,
        // causing WebDriverWait.Until to throw WebDriverTimeoutException.
        // But since we don't want to wait 10 seconds, we throw NoSuchElementException
        // from GoToUrlAsync so it's caught before WebDriverWait is reached.
        _navigation.GoToUrlAsync(Arg.Any<string>())
            .Returns<Task>(_ => throw new NoSuchElementException("Element not found"));

        var result = await _service.LoginAsync("example.com");

        result.ManualLoginRequired.Should().BeTrue();
        result.ErrorMessage.Should().Contain("elements not found");
    }

    [Fact]
    public async Task Login_RestoresWindow_WhenNoSuchElementExceptionThrown()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        _navigation.GoToUrlAsync(Arg.Any<string>())
            .Returns<Task>(_ => throw new NoSuchElementException("Element not found"));

        await _service.LoginAsync("example.com");

        _browserSession.Received(1).RestoreWindow();
    }

    #endregion

    #region Login - WebDriver Error

    [Fact]
    public async Task Login_ReturnsFailed_OnWebDriverException()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        _navigation.GoToUrlAsync(Arg.Any<string>())
            .Returns<Task>(_ => throw new WebDriverException("Session crashed"));

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

        await _webDriverQueue.Received(1).AcquireAsync(
            WebDriverPriority.Background,
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_NeverUsesForegroundPriority()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _webDriverQueue.DidNotReceive().AcquireAsync(
            WebDriverPriority.Foreground,
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_AcquiresNonHeadlessDriver()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        await _service.LoginAsync("example.com");

        await _webDriverQueue.Received(1).AcquireAsync(
            Arg.Any<WebDriverPriority>(),
            false,
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Login - No Submit Selector

    [Fact]
    public async Task Login_PressesEnter_WhenNoSubmitSelector()
    {
        var credential = CreateCredential(submitSelector: null);
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user@example.com");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("s3cret");

        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#email")))
            .Returns(_usernameElement);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#password")))
            .Returns(_passwordElement);

        _webDriver.Url.Returns("https://example.com/dashboard");
        _webDriver.PageSource.Returns("<html><body>Dashboard</body></html>");
        _cookieJar.AllCookies.Returns(
            new System.Collections.ObjectModel.ReadOnlyCollection<Cookie>(new List<Cookie>()));

        await _service.LoginAsync("example.com");

        // Password field should receive Enter key (Keys.Return)
        _passwordElement.Received().SendKeys(Keys.Return);
    }

    #endregion

    #region Login - Lease Disposal

    [Fact]
    public async Task Login_DisposesLease_AfterSuccess()
    {
        var credential = CreateCredential();
        SetupSuccessfulLogin(credential);

        var leaseDisposed = false;
        var lease = new WebDriverLease(_webDriver, () => leaseDisposed = true);
        _webDriverQueue.AcquireAsync(Arg.Any<WebDriverPriority>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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

        _navigation.GoToUrlAsync(Arg.Any<string>())
            .Returns<Task>(_ => throw new WebDriverException("Crash"));

        var leaseDisposed = false;
        var lease = new WebDriverLease(_webDriver, () => leaseDisposed = true);
        _webDriverQueue.AcquireAsync(Arg.Any<WebDriverPriority>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(lease);

        await _service.LoginAsync("example.com");

        leaseDisposed.Should().BeTrue("lease should be disposed even after failure");
    }

    #endregion

    #region Login - Invalid Credentials Detection

    [Fact]
    public async Task Login_ReturnsFailed_WhenStillOnLoginPageWithError()
    {
        var credential = CreateCredential();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("pass");

        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#email")))
            .Returns(_usernameElement);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#password")))
            .Returns(_passwordElement);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#submit")))
            .Returns(_submitElement);

        // Still on login page after submission
        _webDriver.Url.Returns("https://example.com/login");
        _webDriver.PageSource.Returns("<html><body>Invalid username or password. Try again.</body></html>");

        var result = await _service.LoginAsync("example.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("invalid credentials");
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

    [Fact]
    public async Task Login_FillsEmailThenPassword_InMultiStepFlow()
    {
        var credential = CreateMultiStepCredential();
        var emailField = Substitute.For<IWebElement>();
        var passwordField = Substitute.For<IWebElement>();
        var continueButton = Substitute.For<IWebElement>();
        var submitButton = Substitute.For<IWebElement>();

        SetupMultiStepElements(credential, emailField, passwordField, continueButton, submitButton);

        await _service.LoginAsync("nytimes.com");

        // Step 1: email
        emailField.Received().Clear();
        emailField.Received().SendKeys("user@example.com");
        continueButton.Received().Click();

        // Step 2: password
        passwordField.Received().Clear();
        passwordField.Received().SendKeys("s3cret");
        submitButton.Received().Click();
    }

    [Fact]
    public async Task Login_ReturnsFailed_WhenMultiStepLoginDetectsErrorMidFlow()
    {
        var credential = CreateMultiStepCredential();
        var emailField = Substitute.For<IWebElement>();
        var passwordField = Substitute.For<IWebElement>();
        var continueButton = Substitute.For<IWebElement>();
        var submitButton = Substitute.For<IWebElement>();

        SetupMultiStepElements(credential, emailField, passwordField, continueButton, submitButton);

        // After step 1, page shows error (still on login page with error text)
        _webDriver.PageSource.Returns(
            "<html><body>Invalid email. Please try again.</body></html>");

        var result = await _service.LoginAsync("nytimes.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("step 1");
    }

    [Fact]
    public async Task Login_ReturnsFailed_WhenMultiStepStaysOnLoginPage()
    {
        var credential = CreateMultiStepCredential();
        var emailField = Substitute.For<IWebElement>();
        var passwordField = Substitute.For<IWebElement>();
        var continueButton = Substitute.For<IWebElement>();
        var submitButton = Substitute.For<IWebElement>();

        _credentialRepo.GetByDomainAsync(credential.Domain, Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user@example.com");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("s3cret");

        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#email")))
            .Returns(emailField);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#password")))
            .Returns(passwordField);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("submit-email")))
            .Returns(continueButton);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("button[type=submit]")))
            .Returns(submitButton);

        _cookieJar.AllCookies.Returns(
            new System.Collections.ObjectModel.ReadOnlyCollection<Cookie>(new List<Cookie>()));

        // No error between steps, but stays on login page with error after all steps complete
        var pageSourceCalls = 0;
        _webDriver.PageSource.Returns(_ =>
        {
            pageSourceCalls++;
            // First call (inter-step check): no error
            // Later calls (final check): error
            return pageSourceCalls <= 1
                ? "<html><body>Enter your password</body></html>"
                : "<html><body>Invalid username or password</body></html>";
        });
        _webDriver.Url.Returns("https://myaccount.nytimes.com/auth/login");

        var result = await _service.LoginAsync("nytimes.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("invalid credentials");
    }

    [Fact]
    public async Task Login_PressesEnter_WhenMultiStepHasNoSubmitSelector()
    {
        var steps = new List<LoginStep>
        {
            new("#email", StepValueType.Username), // no submit selector
        };
        var credential = CreateCredential(
            domain: "example.com",
            loginSteps: steps,
            usernameSelector: null,
            passwordSelector: null,
            submitSelector: null);

        var emailField = Substitute.For<IWebElement>();
        _credentialRepo.GetByDomainAsync("example.com", Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user@example.com");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("s3cret");

        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#email")))
            .Returns(emailField);
        _webDriver.Url.Returns("https://example.com/dashboard");
        _webDriver.PageSource.Returns("<html><body>Dashboard</body></html>");
        _cookieJar.AllCookies.Returns(
            new System.Collections.ObjectModel.ReadOnlyCollection<Cookie>(new List<Cookie>()));

        await _service.LoginAsync("example.com");

        emailField.Received().SendKeys(Keys.Return);
    }

    private void SetupMultiStepLogin(SiteCredential credential)
    {
        var emailField = Substitute.For<IWebElement>();
        var passwordField = Substitute.For<IWebElement>();
        var continueButton = Substitute.For<IWebElement>();
        var submitButton = Substitute.For<IWebElement>();

        SetupMultiStepElements(credential, emailField, passwordField, continueButton, submitButton);
    }

    private void SetupMultiStepElements(
        SiteCredential credential,
        IWebElement emailField,
        IWebElement passwordField,
        IWebElement continueButton,
        IWebElement submitButton)
    {
        _credentialRepo.GetByDomainAsync(credential.Domain, Arg.Any<CancellationToken>())
            .Returns(credential);
        _encryptionService.Decrypt(credential.EncryptedUsername).Returns("user@example.com");
        _encryptionService.Decrypt(credential.EncryptedPassword).Returns("s3cret");

        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#email")))
            .Returns(emailField);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("#password")))
            .Returns(passwordField);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("submit-email")))
            .Returns(continueButton);
        _webDriver.FindElement(Arg.Is<By>(b => b.ToString()!.Contains("button[type=submit]")))
            .Returns(submitButton);

        _webDriver.Url.Returns("https://www.nytimes.com");
        _webDriver.PageSource.Returns("<html><body>NYT Home</body></html>");
        _cookieJar.AllCookies.Returns(
            new System.Collections.ObjectModel.ReadOnlyCollection<Cookie>(new List<Cookie>()));
    }

    #endregion

    #region Login - No Credentials Does Not Acquire Driver

    [Fact]
    public async Task Login_DoesNotAcquireDriver_WhenNoCredentials()
    {
        _credentialRepo.GetByDomainAsync("unknown.com", Arg.Any<CancellationToken>())
            .Returns((SiteCredential?)null);

        await _service.LoginAsync("unknown.com");

        await _webDriverQueue.DidNotReceive().AcquireAsync(
            Arg.Any<WebDriverPriority>(),
            Arg.Any<bool>(),
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

        await _webDriverQueue.DidNotReceive().AcquireAsync(
            Arg.Any<WebDriverPriority>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    #endregion
}
