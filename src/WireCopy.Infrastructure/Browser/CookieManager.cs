// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser.Models;

namespace WireCopy.Infrastructure.Browser;

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
        ICookieEncryptionService encryptionService,
        string? cookieFilePath = null)
    {
        _logger = logger;
        _encryptionService = encryptionService;
        _cookieFilePath = cookieFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
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

            var json = await File.ReadAllTextAsync(_cookieFilePath).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<StoredCookie>> LoadCookiesAsync()
    {
        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                _logger.LogDebug("No cookie file found at {Path}, returning empty list", _cookieFilePath);
                return Array.Empty<StoredCookie>();
            }

            var json = await File.ReadAllTextAsync(_cookieFilePath).ConfigureAwait(false);
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
                    return Array.Empty<StoredCookie>();
                }
            }

            if (storage == null)
            {
                return Array.Empty<StoredCookie>();
            }

            List<CookieData>? cookieDataList = null;

            if (storage.Version == 2 && storage.EncryptedData != null)
            {
                try
                {
                    var decrypted = _encryptionService.Decrypt(storage.EncryptedData);
                    var cookieContainer = JsonSerializer.Deserialize<CookieDataContainer>(decrypted);
                    cookieDataList = cookieContainer?.Cookies;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt cookies for loading");
                    return Array.Empty<StoredCookie>();
                }
            }
            else if (storage.Version == 1 && storage.Cookies != null)
            {
                cookieDataList = storage.Cookies;
            }

            if (cookieDataList == null || cookieDataList.Count == 0)
            {
                return Array.Empty<StoredCookie>();
            }

            var now = DateTime.UtcNow;
            var result = cookieDataList
                .Where(c => !c.Expiry.HasValue || c.Expiry.Value > now)
                .Select(c => new StoredCookie(c.Name, c.Value, c.Domain, c.Path, c.Expiry))
                .ToList();

            _logger.LogDebug(
                "Loaded {Count} cookies ({Filtered} expired filtered out)",
                result.Count,
                cookieDataList.Count - result.Count);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading cookies, returning empty list");
            return Array.Empty<StoredCookie>();
        }
    }

    public async Task SaveCookiesAsync(IReadOnlyList<StoredCookie> cookies, CancellationToken cancellationToken = default)
    {
        var cookieDataList = cookies.Select(c => new CookieData
        {
            Name = c.Name,
            Value = c.Value,
            Domain = c.Domain,
            Path = c.Path,
            Expiry = c.Expiry,
        }).ToList();

        var container = new CookieDataContainer
        {
            Cookies = cookieDataList,
            Metadata = new Dictionary<string, string>
            {
                ["user_agent"] = "WireCopy/1.0",
                ["last_used"] = DateTime.UtcNow.ToString("O"),
                ["saved_by"] = "BrowserCookieCapture",
                ["import_time"] = DateTime.UtcNow.ToString("O"),
            },
        };

        var json = JsonSerializer.Serialize(container);
        var encrypted = _encryptionService.Encrypt(json);

        var storage = new CookieStorage
        {
            Version = 2,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            EncryptedData = encrypted,
        };

        var directory = System.IO.Path.GetDirectoryName(_cookieFilePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var storageJson = JsonSerializer.Serialize(storage, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_cookieFilePath, storageJson, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Saved {Count} browser cookies (expires: {ExpiryDate})",
            cookies.Count,
            storage.ExpiresAt);
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
