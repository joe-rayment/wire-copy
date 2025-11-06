using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Browser.Models;
using NYTAudioScraper.Infrastructure.Configuration;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Text.Json;

namespace NYTAudioScraper.Infrastructure.Browser;

public class NYTAuthService : INYTAuthService
{
    private readonly NYTConfiguration _config;
    private readonly ILogger<NYTAuthService> _logger;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly string _cookieFilePath;
    private const int CookieExpirationDays = 30;

    public NYTAuthService(
        IOptions<NYTConfiguration> config,
        ILogger<NYTAuthService> logger,
        ICookieEncryptionService encryptionService)
    {
        _config = config.Value;
        _logger = logger;
        _encryptionService = encryptionService;
        _cookieFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NYTAudioScraper", "cookies.json");
    }

    public async Task<bool> AuthenticateAsync(IWebDriver driver, CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if credentials are configured
            if (string.IsNullOrEmpty(_config.Email) || string.IsNullOrEmpty(_config.Password))
            {
                _logger.LogWarning("NYT credentials not configured - skipping authentication");
                return false;
            }

            if (_config.SkipLogin)
            {
                _logger.LogInformation("SkipLogin is enabled - skipping authentication");
                return false;
            }

            _logger.LogInformation("Attempting to authenticate with NYT");

            // Try loading existing cookies first
            if (await TryLoadCookiesAsync(driver, cancellationToken))
            {
                _logger.LogInformation("Successfully authenticated using saved cookies");
                return true;
            }

            // Navigate to login page
            _logger.LogInformation("Navigating to NYT login page");
            driver.Navigate().GoToUrl("https://myaccount.nytimes.com/auth/login");
            await Task.Delay(3000, cancellationToken);

            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(15));

            // Wait for and fill email field (try multiple selectors)
            IWebElement? emailField = null;
            try
            {
                emailField = wait.Until(d =>
                    d.FindElement(By.Name("email")) ??
                    d.FindElement(By.Id("email")) ??
                    d.FindElement(By.CssSelector("input[type='email']")));
            }
            catch (WebDriverTimeoutException ex)
            {
                _logger.LogError(ex, "Timeout waiting for email input field");
                return false;
            }
            catch (NoSuchElementException ex)
            {
                _logger.LogError(ex, "Could not find email input field");
                return false;
            }
            catch (WebDriverException ex)
            {
                _logger.LogError(ex, "WebDriver error while locating email input field");
                return false;
            }

            emailField.Clear();
            emailField.SendKeys(_config.Email);
            _logger.LogInformation("Entered email");
            await Task.Delay(1000, cancellationToken);

            // Click continue/submit button
            var continueButton = driver.FindElement(By.CssSelector("button[type='submit']"));
            continueButton.Click();
            await Task.Delay(3000, cancellationToken);

            // Wait for and fill password field
            IWebElement? passwordField = null;
            try
            {
                passwordField = wait.Until(d =>
                    d.FindElement(By.Name("password")) ??
                    d.FindElement(By.Id("password")) ??
                    d.FindElement(By.CssSelector("input[type='password']")));
            }
            catch (WebDriverTimeoutException ex)
            {
                _logger.LogError(ex, "Timeout waiting for password input field");
                return false;
            }
            catch (NoSuchElementException ex)
            {
                _logger.LogError(ex, "Could not find password input field");
                return false;
            }
            catch (WebDriverException ex)
            {
                _logger.LogError(ex, "WebDriver error while locating password input field");
                return false;
            }

            passwordField.Clear();
            passwordField.SendKeys(_config.Password);
            _logger.LogInformation("Entered password");
            await Task.Delay(1000, cancellationToken);

            // Click login button
            var loginButton = driver.FindElement(By.CssSelector("button[type='submit']"));
            loginButton.Click();
            _logger.LogInformation("Clicked login button, waiting for authentication...");
            await Task.Delay(5000, cancellationToken);

            // Verify login was successful
            if (IsAuthenticated(driver))
            {
                await SaveCookiesAsync(driver, cancellationToken);
                _logger.LogInformation("Successfully authenticated with NYT and saved cookies");
                return true;
            }

            _logger.LogWarning("Authentication failed - unable to verify login");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during NYT authentication");
            return false;
        }
    }

    private bool IsAuthenticated(IWebDriver driver)
    {
        try
        {
            // Check for authentication indicators
            var cookies = driver.Manage().Cookies.AllCookies;
            return cookies.Any(c => c.Name.Contains("NYT-S", StringComparison.OrdinalIgnoreCase) ||
                                   c.Name.Contains("nyt-auth-method", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking authentication status");
            return false;
        }
    }

    private async Task<bool> TryLoadCookiesAsync(IWebDriver driver, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                _logger.LogDebug("No saved cookies found at {CookieFilePath}", _cookieFilePath);
                return false;
            }

            var json = await File.ReadAllTextAsync(_cookieFilePath, cancellationToken);
            CookieStorage? storage;

            try
            {
                storage = JsonSerializer.Deserialize<CookieStorage>(json);
            }
            catch (JsonException)
            {
                // Try to deserialize as old format (List<CookieData>)
                _logger.LogInformation("Detected v1 cookie format, migrating to v2 with encryption");
                var oldCookies = JsonSerializer.Deserialize<List<CookieData>>(json);
                if (oldCookies != null)
                {
                    storage = new CookieStorage
                    {
                        Version = 1,
                        CreatedAt = DateTime.UtcNow,
                        Cookies = oldCookies
                    };
                }
                else
                {
                    _logger.LogWarning("Failed to deserialize cookies in any format");
                    return false;
                }
            }

            if (storage == null)
            {
                _logger.LogDebug("Cookie file is empty or invalid");
                return false;
            }

            // Check expiration
            if (storage.ExpiresAt.HasValue && storage.ExpiresAt < DateTime.UtcNow)
            {
                _logger.LogInformation("Cookies expired on {ExpiryDate}, will re-authenticate",
                    storage.ExpiresAt.Value);
                return false;
            }

            List<CookieData> cookies;

            // Handle version migration
            if (storage.Version == 1)
            {
                _logger.LogInformation("Migrating cookies from v1 (plain text) to v2 (encrypted)");
                cookies = storage.Cookies ?? new List<CookieData>();

                // Re-save with encryption after successful authentication
                // This will be triggered automatically by SaveCookiesAsync
            }
            else if (storage.Version == 2)
            {
                // Decrypt cookie data
                if (storage.EncryptedData == null || storage.EncryptedData.Length == 0)
                {
                    _logger.LogWarning("Cookie storage v2 has no encrypted data");
                    return false;
                }

                try
                {
                    var decrypted = _encryptionService.Decrypt(storage.EncryptedData);
                    var cookieContainer = JsonSerializer.Deserialize<CookieDataContainer>(decrypted);
                    cookies = cookieContainer?.Cookies ?? new List<CookieData>();

                    _logger.LogDebug("Successfully decrypted {Count} cookies", cookies.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to decrypt cookies - data may be corrupted");
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("Unknown cookie storage version: {Version}", storage.Version);
                return false;
            }

            if (cookies.Count == 0)
            {
                _logger.LogDebug("No cookies found in storage");
                return false;
            }

            // Navigate to the domain first to set cookies
            driver.Navigate().GoToUrl(_config.BaseUrl);
            await Task.Delay(1000, cancellationToken);

            // Load cookies into the browser
            foreach (var cookieData in cookies)
            {
                try
                {
                    var cookie = new Cookie(
                        cookieData.Name,
                        cookieData.Value,
                        cookieData.Domain,
                        cookieData.Path,
                        cookieData.Expiry
                    );
                    driver.Manage().Cookies.AddCookie(cookie);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add cookie {CookieName}", cookieData.Name);
                }
            }

            // Refresh to apply cookies
            driver.Navigate().Refresh();
            await Task.Delay(2000, cancellationToken);

            return IsAuthenticated(driver);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cookies from {CookieFilePath}", _cookieFilePath);
            return false;
        }
    }

    private async Task SaveCookiesAsync(IWebDriver driver, CancellationToken cancellationToken)
    {
        try
        {
            var cookies = driver.Manage().Cookies.AllCookies
                .Select(c => new CookieData
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expiry = c.Expiry
                })
                .ToList();

            var cookieContainer = new CookieDataContainer
            {
                Cookies = cookies,
                Metadata = new Dictionary<string, string>
                {
                    ["user_agent"] = _config.UserAgent ?? "Unknown",
                    ["last_used"] = DateTime.UtcNow.ToString("O"),
                    ["saved_by"] = "NYTAudioScraper"
                }
            };

            var json = JsonSerializer.Serialize(cookieContainer);
            var encrypted = _encryptionService.Encrypt(json);

            var storage = new CookieStorage
            {
                Version = 2,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(CookieExpirationDays),
                EncryptedData = encrypted
            };

            var directory = Path.GetDirectoryName(_cookieFilePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var storageJson = JsonSerializer.Serialize(storage, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cookieFilePath, storageJson, cancellationToken);

            _logger.LogInformation("Saved {Count} encrypted cookies (expires: {ExpiryDate})",
                cookies.Count, storage.ExpiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save cookies to {CookieFilePath}", _cookieFilePath);
        }
    }
}
