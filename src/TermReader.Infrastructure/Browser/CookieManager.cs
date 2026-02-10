// Educational and personal use only.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Browser.Models;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Manages stored authentication cookies.
/// </summary>
public class CookieManager : ICookieManager
{
    private readonly ILogger<CookieManager> _logger;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly string _cookieFilePath;

    public CookieManager(
        ILogger<CookieManager> logger,
        ICookieEncryptionService encryptionService)
    {
        _logger = logger;
        _encryptionService = encryptionService;
        _cookieFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermReader",
            "cookies.json");
    }

    public async Task<CookieInfo?> GetCookieInfoAsync()
    {
        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                _logger.LogInformation("No cookie file found at {Path}", _cookieFilePath);
                return new CookieInfo
                {
                    Exists = false,
                    FilePath = _cookieFilePath
                };
            }

            var json = await File.ReadAllTextAsync(_cookieFilePath);
            CookieStorage? storage;

            try
            {
                storage = JsonSerializer.Deserialize<CookieStorage>(json);
            }
            catch (JsonException)
            {
                // Try to deserialize as old format (List<CookieData>)
                var oldCookies = JsonSerializer.Deserialize<List<CookieData>>(json);
                if (oldCookies != null)
                {
                    storage = new CookieStorage
                    {
                        Version = 1,
                        CreatedAt = File.GetCreationTimeUtc(_cookieFilePath),
                        Cookies = oldCookies
                    };
                }
                else
                {
                    _logger.LogWarning("Failed to deserialize cookies in any format");
                    return null;
                }
            }

            if (storage == null)
            {
                return null;
            }

            int? cookieCount = null;
            if (storage.Version == 1 && storage.Cookies != null)
            {
                cookieCount = storage.Cookies.Count;
            }
            else if (storage.Version == 2 && storage.EncryptedData != null)
            {
                try
                {
                    var decrypted = _encryptionService.Decrypt(storage.EncryptedData);
                    var cookieContainer = JsonSerializer.Deserialize<CookieDataContainer>(decrypted);
                    cookieCount = cookieContainer?.Cookies.Count;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt cookies for info display");
                }
            }

            var info = new CookieInfo
            {
                Exists = true,
                CreatedAt = storage.CreatedAt,
                ExpiresAt = storage.ExpiresAt,
                IsExpired = storage.ExpiresAt.HasValue && storage.ExpiresAt < DateTime.UtcNow,
                IsEncrypted = storage.Version == 2,
                Version = storage.Version,
                CookieCount = cookieCount,
                FilePath = _cookieFilePath
            };

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cookie info");
            return null;
        }
    }

    public Task<bool> ClearCookiesAsync()
    {
        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                _logger.LogInformation("No cookie file to clear");
                return Task.FromResult(false);
            }

            File.Delete(_cookieFilePath);
            _logger.LogInformation("Cleared cookies from {Path}", _cookieFilePath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cookies");
            return Task.FromResult(false);
        }
    }
}
