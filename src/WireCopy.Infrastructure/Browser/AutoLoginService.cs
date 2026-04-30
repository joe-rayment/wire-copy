// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums;
using WireCopy.Domain.ValueObjects.Credentials;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Automates login to paywalled sites using stored credentials and Playwright.
/// Uses the PageAccessQueue with Background priority to avoid blocking user navigation.
/// Falls back to manual login when form selectors are missing or elements cannot be found.
/// </summary>
public class AutoLoginService : IAutoLoginService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly IPageAccessQueue _pageAccessQueue;
    private readonly IBrowserSession _browserSession;
    private readonly ICookieManager _cookieManager;
    private readonly ILogger<AutoLoginService> _logger;

    public AutoLoginService(
        IServiceScopeFactory scopeFactory,
        ICookieEncryptionService encryptionService,
        IPageAccessQueue pageAccessQueue,
        IBrowserSession browserSession,
        ICookieManager cookieManager,
        ILogger<AutoLoginService> logger)
    {
        _scopeFactory = scopeFactory;
        _encryptionService = encryptionService;
        _pageAccessQueue = pageAccessQueue;
        _browserSession = browserSession;
        _cookieManager = cookieManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> HasCredentialsAsync(string domain, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var credentialRepo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
        var credential = await credentialRepo.GetByDomainAsync(domain, cancellationToken).ConfigureAwait(false);
        return credential != null;
    }

    /// <inheritdoc />
    public async Task<AutoLoginResult> LoginAsync(string domain, CancellationToken cancellationToken = default)
    {
        // 1. Look up credentials via scoped repository
        using var scope = _scopeFactory.CreateScope();
        var credentialRepo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
        var credential = await credentialRepo.GetByDomainAsync(domain, cancellationToken).ConfigureAwait(false);

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

        // 4. Acquire browser page with Background priority (non-headless for login forms)
        using var lease = await _pageAccessQueue.AcquireAsync(
            PageAccessPriority.Background, headless: false, cancellationToken).ConfigureAwait(false);
        var page = lease.Page;

        try
        {
            // 5. Navigate to login page
            await page.GotoAsync(loginUrl).ConfigureAwait(false);
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false); // Wait for page load

            // 6. Handle form login
            if (credential.CredentialType == CredentialType.FormLogin)
            {
                // Multi-step login flow (e.g. NYT: email -> Continue -> password -> Log In)
                if (credential.IsMultiStep)
                {
                    var steps = credential.GetLoginSteps()!;
                    return await TryMultiStepLoginAsync(page, credential, username, password, steps, cancellationToken).ConfigureAwait(false);
                }

                // Legacy single-step login
                if (string.IsNullOrEmpty(credential.UsernameSelector) ||
                    string.IsNullOrEmpty(credential.PasswordSelector))
                {
                    await _browserSession.RestoreWindowAsync().ConfigureAwait(false);
                    return AutoLoginResult.RequiresManualLogin(
                        "Login form selectors not configured. Browser opened for manual login.");
                }

                return await TryFormLoginAsync(page, credential, username, password, cancellationToken).ConfigureAwait(false);
            }

            return AutoLoginResult.Failed($"Unsupported credential type: {credential.CredentialType}");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Element", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Login form element not found for {Domain}, falling back to manual", domain);
            await _browserSession.RestoreWindowAsync().ConfigureAwait(false);
            return AutoLoginResult.RequiresManualLogin(
                "Login form elements not found. Browser opened for manual login.");
        }
        catch (PlaywrightException ex)
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

    private static async Task<bool> HasLoginErrorOnPageAsync(IPage page)
    {
        try
        {
            var pageSource = await page.ContentAsync().ConfigureAwait(false);
            return pageSource.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                   pageSource.Contains("incorrect", StringComparison.OrdinalIgnoreCase) ||
                   pageSource.Contains("wrong password", StringComparison.OrdinalIgnoreCase) ||
                   pageSource.Contains("try again", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Ignore page source read errors
            return false;
        }
    }

    private async Task<AutoLoginResult> TryFormLoginAsync(
        IPage page,
        Domain.Entities.Credentials.SiteCredential credential,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        // Wait for and fill username field
        var usernameLocator = page.Locator(credential.UsernameSelector!);
        await usernameLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 }).ConfigureAwait(false);
        await usernameLocator.FillAsync(username).ConfigureAwait(false);

        await Task.Delay(500, cancellationToken).ConfigureAwait(false); // Human-like delay

        // Fill password field
        var passwordLocator = page.Locator(credential.PasswordSelector!);
        await passwordLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 }).ConfigureAwait(false);
        await passwordLocator.FillAsync(password).ConfigureAwait(false);

        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        // Submit the form
        if (!string.IsNullOrEmpty(credential.SubmitSelector))
        {
            var submitLocator = page.Locator(credential.SubmitSelector);
            await submitLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 }).ConfigureAwait(false);
            await submitLocator.ClickAsync().ConfigureAwait(false);
        }
        else
        {
            // No submit selector configured - press Enter on the password field
            await passwordLocator.PressAsync("Enter").ConfigureAwait(false);
        }

        // Wait for login to process (page navigation)
        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);

        // Detect success/failure by checking if we're still on a login page
        var currentUrl = page.Url;

        if (IsStillOnLoginPage(currentUrl))
        {
            if (await HasLoginErrorOnPageAsync(page).ConfigureAwait(false))
            {
                return AutoLoginResult.Failed("Login failed: invalid credentials");
            }

            // Still on login page but no obvious error - might be multi-step
            _logger.LogInformation(
                "Still on login page after submission for {Domain}, treating as possible multi-step login",
                credential.Domain);
        }

        // Capture and persist browser cookies for future sessions
        await CaptureBrowserCookiesAsync(page, credential.Domain, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Login completed for {Domain}, navigated to {Url}", credential.Domain, currentUrl);
        return AutoLoginResult.Succeeded();
    }

    private async Task<AutoLoginResult> TryMultiStepLoginAsync(
        IPage page,
        Domain.Entities.Credentials.SiteCredential credential,
        string username,
        string password,
        List<LoginStep> steps,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var stepNum = i + 1;

            _logger.LogDebug(
                "Executing login step {Step}/{Total} for {Domain}: field={Selector}, type={Type}",
                stepNum,
                steps.Count,
                credential.Domain,
                step.FieldSelector,
                step.ValueType);

            // Determine which value to fill
            var value = step.ValueType == StepValueType.Username ? username : password;

            // Wait for and fill the input field
            var fieldLocator = page.Locator(step.FieldSelector);
            await fieldLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 }).ConfigureAwait(false);
            await fieldLocator.FillAsync(value).ConfigureAwait(false);

            await Task.Delay(500, cancellationToken).ConfigureAwait(false); // Human-like delay

            // Submit: click button or press Enter
            if (!string.IsNullOrEmpty(step.SubmitSelector))
            {
                var submitLocator = page.Locator(step.SubmitSelector);
                await submitLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 }).ConfigureAwait(false);
                await submitLocator.ClickAsync().ConfigureAwait(false);
            }
            else
            {
                await fieldLocator.PressAsync("Enter").ConfigureAwait(false);
            }

            // Wait for the page to transition between steps
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

            // After each step (except the last), check for errors
            if (stepNum < steps.Count && await HasLoginErrorOnPageAsync(page).ConfigureAwait(false))
            {
                return AutoLoginResult.Failed($"Login failed at step {stepNum}: error detected on page");
            }
        }

        // Final wait for redirect after last step
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

        var currentUrl = page.Url;

        if (IsStillOnLoginPage(currentUrl) && await HasLoginErrorOnPageAsync(page).ConfigureAwait(false))
        {
            return AutoLoginResult.Failed("Login failed: invalid credentials");
        }

        // Capture and persist browser cookies
        await CaptureBrowserCookiesAsync(page, credential.Domain, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Multi-step login completed for {Domain}, navigated to {Url}", credential.Domain, currentUrl);
        return AutoLoginResult.Succeeded();
    }

    private async Task CaptureBrowserCookiesAsync(IPage page, string domain, CancellationToken cancellationToken)
    {
        try
        {
            var playwrightCookies = await page.Context.CookiesAsync().ConfigureAwait(false);
            _logger.LogDebug(
                "Browser has {Count} cookies after login for {Domain}",
                playwrightCookies.Count,
                domain);

            if (playwrightCookies.Count == 0)
            {
                return;
            }

            var storedCookies = playwrightCookies.Select(c =>
                new StoredCookie(
                    c.Name,
                    c.Value,
                    c.Domain ?? string.Empty,
                    c.Path ?? string.Empty,
                    c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).DateTime : null)).ToList();

            await _cookieManager.SaveCookiesAsync(storedCookies, cancellationToken).ConfigureAwait(false);
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
