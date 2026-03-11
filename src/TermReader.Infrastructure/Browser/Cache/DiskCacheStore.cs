// Educational and personal use only.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.Cache;

/// <summary>
/// Disk-backed persistence for page cache entries.
/// Each entry is stored as an individual JSON file named by SHA256 hash of the normalized URL.
/// Uses atomic writes (tmp-then-rename) to prevent corruption.
/// </summary>
internal sealed class DiskCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _cacheDirectory;
    private readonly ILogger _logger;

    public DiskCacheStore(string cacheDirectory, ILogger logger)
    {
        _cacheDirectory = cacheDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Loads all non-expired cache entries from disk.
    /// Corrupt or expired files are deleted during load.
    /// </summary>
    public List<(PageLoadResult Result, CacheEntryMetadata Metadata)> LoadAll()
    {
        var results = new List<(PageLoadResult, CacheEntryMetadata)>();

        if (!Directory.Exists(_cacheDirectory))
        {
            return results;
        }

        // Clean up orphaned .tmp files from interrupted writes
        foreach (var tmpFile in Directory.EnumerateFiles(_cacheDirectory, "*.tmp"))
        {
            try
            {
                File.Delete(tmpFile);
            }
            catch
            {
                // Best effort cleanup
            }
        }

        foreach (var filePath in Directory.EnumerateFiles(_cacheDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var persisted = JsonSerializer.Deserialize<PersistedCacheEntry>(json, JsonOptions);
                if (persisted == null)
                {
                    File.Delete(filePath);
                    continue;
                }

                if (DateTime.UtcNow >= persisted.ExpiresAtUtc)
                {
                    File.Delete(filePath);
                    continue;
                }

                var metadata = new CacheEntryMetadata
                {
                    RequestUrl = persisted.RequestUrl,
                    FinalUrl = persisted.FinalUrl,
                    NormalizedUrl = persisted.NormalizedUrl,
                    CachedAtUtc = persisted.CachedAtUtc,
                    ExpiresAtUtc = persisted.ExpiresAtUtc,
                    LastAccessedAtUtc = persisted.LastAccessedAtUtc,
                    SizeBytes = persisted.SizeBytes,
                };

                var pageMetadata = new PageMetadata
                {
                    Title = persisted.Title ?? string.Empty,
                    Description = persisted.Description,
                    CanonicalUrl = persisted.CanonicalUrl,
                    Author = persisted.Author,
                    PublishedDate = persisted.PublishedDate,
                    FaviconUrl = persisted.FaviconUrl,
                };

                var result = new PageLoadResult
                {
                    Success = true,
                    Url = persisted.FinalUrl,
                    Html = persisted.Html,
                    Metadata = pageMetadata,
                    StatusCode = persisted.StatusCode,
                    FetchMethod = (FetchMethod)persisted.FetchMethod,
                };

                results.Add((result, metadata));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cache entry from {Path}, deleting", filePath);
                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        _logger.LogInformation("Loaded {Count} cache entries from disk", results.Count);
        return results;
    }

    /// <summary>
    /// Writes a cache entry to disk atomically.
    /// </summary>
    public void Write(string normalizedUrl, PageLoadResult result, CacheEntryMetadata metadata)
    {
        try
        {
            EnsureDirectoryExists();

            var persisted = new PersistedCacheEntry
            {
                RequestUrl = metadata.RequestUrl,
                FinalUrl = metadata.FinalUrl,
                NormalizedUrl = normalizedUrl,
                Html = result.Html,
                StatusCode = result.StatusCode,
                FetchMethod = (int)result.FetchMethod,
                Title = result.Metadata?.Title,
                Description = result.Metadata?.Description,
                CanonicalUrl = result.Metadata?.CanonicalUrl,
                Author = result.Metadata?.Author,
                PublishedDate = result.Metadata?.PublishedDate,
                FaviconUrl = result.Metadata?.FaviconUrl,
                CachedAtUtc = metadata.CachedAtUtc,
                ExpiresAtUtc = metadata.ExpiresAtUtc,
                LastAccessedAtUtc = metadata.LastAccessedAtUtc,
                SizeBytes = metadata.SizeBytes,
            };

            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            var filePath = GetFilePath(normalizedUrl);
            var tmpPath = filePath + ".tmp";

            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cache entry for {Url}", normalizedUrl);
        }
    }

    /// <summary>
    /// Deletes a single cache entry from disk.
    /// </summary>
    public void Delete(string normalizedUrl)
    {
        try
        {
            var filePath = GetFilePath(normalizedUrl);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache entry for {Url}", normalizedUrl);
        }
    }

    /// <summary>
    /// Deletes all cache entries from disk.
    /// </summary>
    public void ClearAll()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.tmp"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear disk cache");
        }
    }

    internal string GetFilePath(string normalizedUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
        var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".json";
        return Path.Combine(_cacheDirectory, fileName);
    }

    private void EnsureDirectoryExists()
    {
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Flat DTO for JSON serialization, decoupled from domain types for version resilience.
    /// </summary>
    internal sealed class PersistedCacheEntry
    {
        public string RequestUrl { get; set; } = string.Empty;

        public string FinalUrl { get; set; } = string.Empty;

        public string NormalizedUrl { get; set; } = string.Empty;

        public string Html { get; set; } = string.Empty;

        public int StatusCode { get; set; }

        public int FetchMethod { get; set; }

        public string? Title { get; set; }

        public string? Description { get; set; }

        public string? CanonicalUrl { get; set; }

        public string? Author { get; set; }

        public DateTime? PublishedDate { get; set; }

        public string? FaviconUrl { get; set; }

        public DateTime CachedAtUtc { get; set; }

        public DateTime ExpiresAtUtc { get; set; }

        public DateTime LastAccessedAtUtc { get; set; }

        public long SizeBytes { get; set; }
    }
}
