// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Automates login to paywalled sites using stored credentials and Selenium.
/// Uses the WebDriverQueue with Background priority to avoid blocking user navigation.
/// Falls back to manual login when form selectors are missing or elements cannot be found.
/// </summary>
public class AutoLoginService : IAutoLoginService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly IWebDriverQueue _webDriverQueue;
    private readonly IBrowserSession _browserSession;
    private readonly ICookieManager _cookieManager;
    private readonly ILogger<AutoLoginService> _logger;

    public AutoLoginService(
        IServiceScopeFactory scopeFactory,
        ICookieEncryptionService encryptionService,
        IWebDriverQueue webDriverQueue,
        IBrowserSession browserSession,
        ICookieManager cookieManager,
        ILogger<AutoLoginService> logger)
    {
        _scopeFactory = scopeFactory;
        _encryptionService = encryptionService;
        _webDriverQueue = webDriverQueue;
        _browserSession = browserSession;
        _cookieManager = cookieManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> HasCredentialsAsync(string domain, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var credentialRepo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
        var credential = await credentialRepo.GetByDomainAsync(domain, cancellationToken);
        return credential != null;
    }

    /// <inheritdoc />
    public async Task<AutoLoginResult> LoginAsync(string domain, CancellationToken cancellationToken = default)
    {
        // 1. Look up credentials via scoped repository
        using var scope = _scopeFactory.CreateScope();
        var credentialRepo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
        var credential = await credentialRepo.GetByDomainAsync(domain, cancellationToken);

        if (credential == null)
        {
            return AutoLoginResult.Failed($"No credentials stored for {domain}");
        }

        // 2. Decrypt username and password
        string username;
        string password;
        try
        {
            username = _encryptionService.Decrypt(credential.EncryptedUsername);
            password = _encryptionService.Decrypt(credential.EncryptedPassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt credentials for {Domain}", domain);
            return AutoLoginResult.Failed("Failed to decrypt stored credentials");
        }

        // 3. Determine login URL
        var loginUrl = credential.LoginUrl ?? $"https://{domain}/login";

        // 4. Acquire WebDriver with Background priority (non-headless for login forms)
        using var lease = await _webDriverQueue.AcquireAsync(
            WebDriverPriority.Background, headless: false, cancellationToken);
        var driver = lease.Driver;

        try
        {
            // 5. Navigate to login page
            await driver.Navigate().GoToUrlAsync(loginUrl);
            await Task.Delay(2000, cancellationToken); // Wait for page load

            // 6. Handle form login
            if (credential.CredentialType == CredentialType.FormLogin)
            {
                if (string.IsNullOrEmpty(credential.UsernameSelector) ||
                    string.IsNullOrEmpty(credential.PasswordSelector))
                {
                    _browserSession.RestoreWindow();
                    return AutoLoginResult.RequiresManualLogin(
                        "Login form selectors not configured. Browser opened for manual login.");
                }

                return await TryFormLoginAsync(driver, credential, username, password, cancellationToken);
            }

            return AutoLoginResult.Failed($"Unsupported credential type: {credential.CredentialType}");
        }
        catch (NoSuchElementException ex)
        {
            _logger.LogWarning(ex, "Login form element not found for {Domain}, falling back to manual", domain);
            _browserSession.RestoreWindow();
            return AutoLoginResult.RequiresManualLogin(
                "Login form elements not found. Browser opened for manual login.");
        }
        catch (WebDriverException ex)
        {
            _logger.LogError(ex, "Browser error during login for {Domain}", domain);
            return AutoLoginResult.Failed($"Browser error: {ex.Message}");
        }
    }

    private static bool IsStillOnLoginPage(string currentUrl)
    {
        return currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               currentUrl.Contains("/signin", StringComparison.OrdinalIgnoreCase) ||
               currentUrl.Contains("/sign-in", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLoginErrorOnPage(IWebDriver driver)
    {
        try
        {
            var pageSource = driver.PageSource.ToLowerInvariant();
            return pageSource.Contains("invalid", StringComparison.Ordinal) ||
                   pageSource.Contains("incorrect", StringComparison.Ordinal) ||
                   pageSource.Contains("wrong password", StringComparison.Ordinal) ||
                   pageSource.Contains("try again", StringComparison.Ordinal);
        }
        catch
        {
            // Ignore page source read errors
            return false;
        }
    }

    private async Task<AutoLoginResult> TryFormLoginAsync(
        IWebDriver driver,
        Domain.Entities.Credentials.SiteCredential credential,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));

        // Wait for and fill username field
        var usernameField = wait.Until(d =>
            d.FindElement(By.CssSelector(credential.UsernameSelector!)));
        usernameField.Clear();
        usernameField.SendKeys(username);

        await Task.Delay(500, cancellationToken); // Human-like delay

        // Fill password field
        var passwordField = wait.Until(d =>
            d.FindElement(By.CssSelector(credential.PasswordSelector!)));
        passwordField.Clear();
        passwordField.SendKeys(password);

        await Task.Delay(500, cancellationToken);

        // Submit the form
        if (!string.IsNullOrEmpty(credential.SubmitSelector))
        {
            var submitButton = wait.Until(d =>
                d.FindElement(By.CssSelector(credential.SubmitSelector)));
            submitButton.Click();
        }
        else
        {
            // No submit selector configured - press Enter on the password field
            passwordField.SendKeys(Keys.Return);
        }

        // Wait for login to process (page navigation)
        await Task.Delay(3000, cancellationToken);

        // Detect success/failure by checking if we're still on a login page
        var currentUrl = driver.Url;

        if (IsStillOnLoginPage(currentUrl))
        {
            if (HasLoginErrorOnPage(driver))
            {
                return AutoLoginResult.Failed("Login failed: invalid credentials");
            }

            // Still on login page but no obvious error - might be multi-step
            _logger.LogInformation(
                "Still on login page after submission for {Domain}, treating as possible multi-step login",
                credential.Domain);
        }

        // Capture and persist browser cookies for future sessions
        await CaptureBrowserCookiesAsync(driver, credential.Domain, cancellationToken);

        _logger.LogInformation("Login completed for {Domain}, navigated to {Url}", credential.Domain, currentUrl);
        return AutoLoginResult.Succeeded();
    }

    private async Task CaptureBrowserCookiesAsync(IWebDriver driver, string domain, CancellationToken cancellationToken)
    {
        try
        {
            var seleniumCookies = driver.Manage().Cookies.AllCookies;
            _logger.LogDebug(
                "Browser has {Count} cookies after login for {Domain}",
                seleniumCookies.Count,
                domain);

            if (seleniumCookies.Count == 0)
            {
                return;
            }

            var storedCookies = seleniumCookies.Select(c =>
                new StoredCookie(
                    c.Name,
                    c.Value,
                    c.Domain,
                    c.Path,
                    c.Expiry)).ToList();

            await _cookieManager.SaveCookiesAsync(storedCookies, cancellationToken);
            _logger.LogInformation(
                "Persisted {Count} cookies after login for {Domain}",
                storedCookies.Count,
                domain);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture cookies after login for {Domain}", domain);
        }
    }
}
