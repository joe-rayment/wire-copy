// <copyright file="CookieImporter.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using System.Text.Json;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Browser.Models;

namespace NYTAudioScraper.Infrastructure.Browser;

public class CookieImporter
{
    private const int CookieExpirationDays = 30;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly ILogger<CookieImporter> _logger;
    private readonly string _cookieFilePath;

    public CookieImporter(
        ICookieEncryptionService encryptionService,
        ILogger<CookieImporter> logger)
    {
        _encryptionService = encryptionService;
        _logger = logger;
        _cookieFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NYTAudioScraper",
            "cookies.json");
    }

    public async Task<CookieImportResult> ImportFromJsonAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Importing cookies from {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                return new CookieImportResult
                {
                    Success = false,
                    ErrorMessage = $"Cookie file not found: {filePath}"
                };
            }

            var json = await File.ReadAllTextAsync(filePath, cancellationToken);

            // Try to parse as Chrome DevTools format (array of cookie objects)
            List<CookieData>? cookies;
            try
            {
                cookies = JsonSerializer.Deserialize<List<CookieData>>(json);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse cookie file");
                return new CookieImportResult
                {
                    Success = false,
                    ErrorMessage = $"Invalid JSON format: {ex.Message}"
                };
            }

            if (cookies == null || cookies.Count == 0)
            {
                return new CookieImportResult
                {
                    Success = false,
                    ErrorMessage = "No cookies found in file"
                };
            }

            // Filter for NYT cookies only
            var nytCookies = cookies.Where(c =>
                c.Domain?.Contains("nytimes.com", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            if (nytCookies.Count == 0)
            {
                return new CookieImportResult
                {
                    Success = false,
                    ErrorMessage = "No NYT cookies found in file. Make sure cookies are from nytimes.com domain."
                };
            }

            // Check for authentication cookies
            var hasAuthCookie = nytCookies.Any(c =>
                c.Name.Contains("NYT-S", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("nyt-auth-method", StringComparison.OrdinalIgnoreCase));

            if (!hasAuthCookie)
            {
                _logger.LogWarning("No authentication cookies found. Imported cookies may not provide authenticated access.");
            }

            // Calculate expiration
            var oldestExpiry = nytCookies
                .Where(c => c.Expiry.HasValue)
                .Select(c => c.Expiry!.Value)
                .OrderBy(e => e)
                .FirstOrDefault();

            var expiresAt = oldestExpiry != default
                ? oldestExpiry
                : DateTime.UtcNow.AddDays(CookieExpirationDays);

            // Save cookies
            await SaveCookiesAsync(nytCookies, expiresAt, cancellationToken);

            var daysUntilExpiration = (expiresAt - DateTime.UtcNow).Days;

            return new CookieImportResult
            {
                Success = true,
                CookieCount = nytCookies.Count,
                HasAuthCookie = hasAuthCookie,
                ExpiresAt = expiresAt,
                DaysUntilExpiration = daysUntilExpiration
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing cookies");
            return new CookieImportResult
            {
                Success = false,
                ErrorMessage = $"Unexpected error: {ex.Message}"
            };
        }
    }

    public async Task<CookieInfoResult> GetCookieInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                return new CookieInfoResult
                {
                    HasCookies = false,
                    Message = "No cookies stored"
                };
            }

            var json = await File.ReadAllTextAsync(_cookieFilePath, cancellationToken);
            var storage = JsonSerializer.Deserialize<CookieStorage>(json);

            if (storage == null)
            {
                return new CookieInfoResult
                {
                    HasCookies = false,
                    Message = "Cookie file is empty or invalid"
                };
            }

            List<CookieData> cookies;

            if (storage.Version == 1)
            {
                cookies = storage.Cookies ?? new List<CookieData>();
            }
            else if (storage.Version == 2)
            {
                if (storage.EncryptedData == null || storage.EncryptedData.Length == 0)
                {
                    return new CookieInfoResult
                    {
                        HasCookies = false,
                        Message = "Cookie storage has no encrypted data"
                    };
                }

                var decrypted = _encryptionService.Decrypt(storage.EncryptedData);
                var cookieContainer = JsonSerializer.Deserialize<CookieDataContainer>(decrypted);
                cookies = cookieContainer?.Cookies ?? new List<CookieData>();
            }
            else
            {
                return new CookieInfoResult
                {
                    HasCookies = false,
                    Message = $"Unknown cookie storage version: {storage.Version}"
                };
            }

            var hasAuthCookie = cookies.Any(c =>
                c.Name.Contains("NYT-S", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("nyt-auth-method", StringComparison.OrdinalIgnoreCase));

            var daysUntilExpiration = storage.ExpiresAt.HasValue
                ? (storage.ExpiresAt.Value - DateTime.UtcNow).Days
                : 0;

            return new CookieInfoResult
            {
                HasCookies = true,
                CookieCount = cookies.Count,
                HasAuthCookie = hasAuthCookie,
                CreatedAt = storage.CreatedAt,
                ExpiresAt = storage.ExpiresAt,
                DaysUntilExpiration = daysUntilExpiration,
                IsExpired = storage.ExpiresAt.HasValue && storage.ExpiresAt < DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cookie info");
            return new CookieInfoResult
            {
                HasCookies = false,
                Message = $"Error reading cookies: {ex.Message}"
            };
        }
    }

    public async Task<bool> ClearCookiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_cookieFilePath))
            {
                File.Delete(_cookieFilePath);
                _logger.LogInformation("Cleared stored cookies");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cookies");
            return false;
        }
    }

    private async Task SaveCookiesAsync(List<CookieData> cookies, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var cookieContainer = new CookieDataContainer
        {
            Cookies = cookies,
            Metadata = new Dictionary<string, string>
            {
                ["user_agent"] = "NYTAudioScraper/1.0",
                ["last_used"] = DateTime.UtcNow.ToString("O"),
                ["saved_by"] = "CookieImporter",
                ["import_time"] = DateTime.UtcNow.ToString("O")
            }
        };

        var json = JsonSerializer.Serialize(cookieContainer);
        var encrypted = _encryptionService.Encrypt(json);

        var storage = new CookieStorage
        {
            Version = 2,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            EncryptedData = encrypted
        };

        var directory = Path.GetDirectoryName(_cookieFilePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var storageJson = JsonSerializer.Serialize(storage, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_cookieFilePath, storageJson, cancellationToken);

        _logger.LogInformation("Saved {Count} encrypted cookies (expires: {ExpiryDate})", cookies.Count, expiresAt);
    }
}

public class CookieImportResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int CookieCount { get; set; }
    public bool HasAuthCookie { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int DaysUntilExpiration { get; set; }
}

public class CookieInfoResult
{
    public bool HasCookies { get; set; }
    public int CookieCount { get; set; }
    public bool HasAuthCookie { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int DaysUntilExpiration { get; set; }
    public bool IsExpired { get; set; }
    public string? Message { get; set; }
}
