// <copyright file="NYTAuthService.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Browser.Models;
using NYTAudioScraper.Infrastructure.Configuration;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace NYTAudioScraper.Infrastructure.Browser;

public class NYTAuthService : INYTAuthService
{
    private const int CookieExpirationDays = 30;
    private readonly NYTConfiguration _config;
    private readonly ILogger<NYTAuthService> _logger;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly string _cookieFilePath;

    public NYTAuthService(
        IOptions<NYTConfiguration> config,
        ILogger<NYTAuthService> logger,
        ICookieEncryptionService encryptionService)
    {
        _config = config.Value;
        _logger = logger;
        _encryptionService = encryptionService;
        _cookieFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NYTAudioScraper",
            "cookies.json");
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

            // Saved cookies not found or expired - prompt for manual login
            _logger.LogInformation("No saved cookies found or cookies expired");
            return await PromptManualLoginAsync(driver, cancellationToken);
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

            // Log all cookies for debugging
            _logger.LogDebug("Checking authentication. Current URL: {Url}", driver.Url);
            _logger.LogDebug("Found {Count} cookies:", cookies.Count);
            foreach (var cookie in cookies)
            {
                _logger.LogDebug("  Cookie: {Name} = {Value}", cookie.Name, cookie.Value?.Substring(0, Math.Min(20, cookie.Value?.Length ?? 0)) + "...");
            }

            // Check for NYT authentication cookies
            var hasAuthCookie = cookies.Any(c =>
                c.Name.Contains("NYT-S", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("nyt-auth-method", StringComparison.OrdinalIgnoreCase));

            if (hasAuthCookie)
            {
                _logger.LogInformation("✓ Found NYT authentication cookie");
            }
            else
            {
                _logger.LogWarning("✗ No NYT authentication cookies found (looking for 'NYT-S' or 'nyt-auth-method')");
            }

            return hasAuthCookie;
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
#pragma warning disable S6966 // Selenium WebDriver 4.26.1 does not provide async navigation methods
            driver.Navigate().GoToUrl(_config.BaseUrl);
#pragma warning restore S6966
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
                        cookieData.Expiry);
                    driver.Manage().Cookies.AddCookie(cookie);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add cookie {CookieName}", cookieData.Name);
                }
            }

            // Refresh to apply cookies
#pragma warning disable S6966 // Selenium WebDriver 4.26.1 does not provide async navigation methods
            driver.Navigate().Refresh();
#pragma warning restore S6966
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
                    ["user_agent"] = "NYTAudioScraper/1.0",
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

            _logger.LogInformation(
                "Saved {Count} encrypted cookies (expires: {ExpiryDate})",
                cookies.Count,
                storage.ExpiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save cookies to {CookieFilePath}", _cookieFilePath);
        }
    }

    private async Task<bool> PromptManualLoginAsync(IWebDriver driver, CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║          NYT Authentication Required                                  ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("To avoid bot detection, please export your auth cookie from Chrome:");
            Console.WriteLine();
            Console.WriteLine("STEPS:");
            Console.WriteLine("  1. Open Chrome and go to https://www.nytimes.com");
            Console.WriteLine("  2. Make sure you're logged in");
            Console.WriteLine("  3. Press F12 or CMD+Option+I to open DevTools");
            Console.WriteLine("  4. Click the 'Application' tab (may be under >> menu)");
            Console.WriteLine("  5. In left sidebar: Expand 'Cookies' → Click 'https://www.nytimes.com'");
            Console.WriteLine("  6. Find the cookie named 'NYT-S' in the table");
            Console.WriteLine("  7. Double-click its Value to select, then copy it (CMD+C)");
            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────────────────────────────────────");
            Console.WriteLine();
            Console.Write("Paste the NYT-S cookie value here: ");

            var cookieValue = Console.ReadLine()?.Trim();

            // Validate input
            if (string.IsNullOrWhiteSpace(cookieValue))
            {
                Console.WriteLine();
                Console.WriteLine("✗ No cookie value provided. Continuing without authentication.");
                Console.WriteLine();
                return false;
            }

            // Security: Validate cookie length to prevent DoS
            const int MaxCookieLength = 8192; // 8KB is reasonable for cookies
            if (cookieValue.Length > MaxCookieLength)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Cookie value too long. Maximum {MaxCookieLength} characters allowed.");
                Console.WriteLine("  This doesn't look like a valid NYT cookie.");
                Console.WriteLine();
                return false;
            }

            // Security: Validate cookie format (cookies should be base64/alphanumeric with some special chars)
            if (!System.Text.RegularExpressions.Regex.IsMatch(cookieValue, @"^[a-zA-Z0-9\-_=.]+$"))
            {
                Console.WriteLine();
                Console.WriteLine("✗ Invalid cookie format. Cookie should only contain alphanumeric characters, hyphens, underscores, equals signs, and periods.");
                Console.WriteLine("  Please make sure you copied the cookie value correctly.");
                Console.WriteLine();
                return false;
            }

            // Create cookie structure and save
            var cookies = new List<CookieData>
            {
                new CookieData
                {
                    Name = "NYT-S",
                    Value = cookieValue,
                    Domain = ".nytimes.com",
                    Path = "/",
                    Expiry = DateTime.UtcNow.AddDays(CookieExpirationDays)
                },
                new CookieData
                {
                    Name = "nyt-auth-method",
                    Value = "username",
                    Domain = ".nytimes.com",
                    Path = "/",
                    Expiry = DateTime.UtcNow.AddDays(CookieExpirationDays)
                }
            };

            // Save cookies
            var cookieContainer = new CookieDataContainer
            {
                Cookies = cookies,
                Metadata = new Dictionary<string, string>
                {
                    ["user_agent"] = "NYTAudioScraper/1.0",
                    ["last_used"] = DateTime.UtcNow.ToString("O"),
                    ["saved_by"] = "Interactive Prompt"
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(cookieContainer);
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

            var storageJson = System.Text.Json.JsonSerializer.Serialize(storage,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cookieFilePath, storageJson, cancellationToken);

            Console.WriteLine();
            Console.WriteLine("✓ Cookie saved successfully!");
            Console.WriteLine($"  Expires: {storage.ExpiresAt:yyyy-MM-dd}");
            Console.WriteLine("  Future runs will use this cookie automatically.");
            Console.WriteLine();

            _logger.LogInformation("Cookie saved via interactive prompt (expires: {ExpiryDate})", storage.ExpiresAt);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during interactive cookie prompt");
            Console.WriteLine();
            Console.WriteLine("✗ Error saving cookie. Please try again.");
            Console.WriteLine();
            return false;
        }
    }
}
