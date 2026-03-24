// Educational and personal use only.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Browser.Models;

namespace TermReader.Infrastructure.Browser;

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
            "TermReader",
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

            // Security: Validate path is in allowed directory and not a symlink
            var fullPath = Path.GetFullPath(filePath);
            var pathValidation = ValidateCookieFilePath(fullPath);
            if (!pathValidation.IsValid)
            {
                _logger.LogWarning("Rejected cookie import from disallowed path: {FilePath}", filePath);
                return new CookieImportResult
                {
                    Success = false,
                    ErrorMessage = pathValidation.ErrorMessage ?? "Cookie file path is not allowed"
                };
            }

            var json = await File.ReadAllTextAsync(fullPath, cancellationToken);

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
            var hasAuthCookie = nytCookies.Exists(c =>
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

            var hasAuthCookie = cookies.Exists(c =>
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

    public Task<bool> ClearCookiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                return Task.FromResult(false);
            }

            File.Delete(_cookieFilePath);
            _logger.LogInformation("Cleared stored cookies");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cookies");
            return Task.FromResult(false);
        }
    }

    private static (bool IsValid, string? ErrorMessage) ValidateCookieFilePath(string fullPath)
    {
        // Security: Whitelist approach - only allow cookie imports from safe directories
        // This prevents reading sensitive system files like /etc/passwd, /root/.ssh/*, etc.

        // Check if file is a symlink (security risk - could point to sensitive files)
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Exists && fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return (false, "Symbolic links are not allowed for security reasons");
        }

        // Define allowed base directories (whitelist)
        var allowedDirectories = new List<string>
        {
            // User's home directory and common subdirectories
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),

            // Downloads folder (platform-specific handling)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),

            // System temp directory (safe for temporary cookie exports)
            Path.GetTempPath(),

            // Current working directory (for development/testing)
            Directory.GetCurrentDirectory(),
        };

        // On macOS/Linux, also allow common external mount points
        if (!OperatingSystem.IsWindows())
        {
            allowedDirectories.Add("/Volumes"); // macOS external drives
            allowedDirectories.Add("/media");   // Linux external drives
            allowedDirectories.Add("/mnt");     // Linux mount points
        }

        // Normalize all paths for comparison (handles trailing slashes, etc.)
        var normalizedPath = Path.GetFullPath(fullPath);
        var normalizedAllowedDirs = allowedDirectories
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(Path.GetFullPath)
            .ToList();

        // Check if file path starts with any allowed directory
        var isInAllowedDirectory = normalizedAllowedDirs.Exists(allowedDir =>
            normalizedPath.StartsWith(allowedDir, StringComparison.Ordinal));

        if (!isInAllowedDirectory)
        {
            return (false, "Cookie file must be in user directories (Home, Downloads, Documents, Desktop, or /tmp). " +
                           "For security, system directories are not allowed.");
        }

        return (true, null);
    }

    private async Task SaveCookiesAsync(List<CookieData> cookies, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var cookieContainer = new CookieDataContainer
        {
            Cookies = cookies,
            Metadata = new Dictionary<string, string>
            {
                ["user_agent"] = "TermReader/1.0",
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
