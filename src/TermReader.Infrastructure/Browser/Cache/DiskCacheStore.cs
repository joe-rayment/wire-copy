// Educational and personal use only.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Browser;
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
    private readonly long _maxDiskSizeBytes;
    private readonly ILogger _logger;

    public DiskCacheStore(string cacheDirectory, ILogger logger, long maxDiskSizeBytes = 500L * 1024 * 1024)
    {
        _cacheDirectory = cacheDirectory;
        _maxDiskSizeBytes = maxDiskSizeBytes;
        _logger = logger;
    }

    /// <summary>
    /// Gets the configured maximum disk cache size in bytes.
    /// </summary>
    public long MaxDiskSizeBytes => _maxDiskSizeBytes;

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

        foreach (var filePath in Directory.EnumerateFiles(_cacheDirectory, "*.json")
                     .Where(f => !f.EndsWith(".build.json", StringComparison.OrdinalIgnoreCase)))
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

        EnforceSizeLimit();

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

            EnforceSizeLimit();
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
    /// Deletes a build cache entry from disk.
    /// </summary>
    public void DeleteBuildCache(string normalizedUrl)
    {
        try
        {
            var filePath = GetBuildCacheFilePath(normalizedUrl);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete build cache entry for {Url}", normalizedUrl);
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

    /// <summary>
    /// Gets the number of .json cache files on disk.
    /// </summary>
    public int GetFileCount()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return 0;
        }

        return Directory.GetFiles(_cacheDirectory, "*.json")
            .Count(f => !f.EndsWith(".build.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the total size of all .json cache files on disk in bytes.
    /// </summary>
    public long GetTotalSizeBytes()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return 0;
        }

        return new DirectoryInfo(_cacheDirectory)
            .EnumerateFiles("*.json")
            .Where(f => !f.Name.EndsWith(".build.json", StringComparison.OrdinalIgnoreCase))
            .Sum(f => f.Length);
    }

    /// <summary>
    /// Writes a PageBuildCache to disk as a separate .build.json file.
    /// </summary>
    public void WriteBuildCache(string normalizedUrl, PageBuildCache buildCache)
    {
        try
        {
            EnsureDirectoryExists();

            var persisted = new PersistedBuildCache
            {
                FinalUrl = buildCache.FinalUrl,
                Classification = (int)buildCache.Classification,
                ClassificationVersion = buildCache.ClassificationVersion,
                CachedAt = buildCache.CachedAt,
                Title = buildCache.Metadata.Title,
                MetaDescription = buildCache.Metadata.Description,
                MetaCanonicalUrl = buildCache.Metadata.CanonicalUrl,
                MetaAuthor = buildCache.Metadata.Author,
                MetaPublishedDate = buildCache.Metadata.PublishedDate,
                MetaFaviconUrl = buildCache.Metadata.FaviconUrl,
                Links = buildCache.Links.Select(l => new PersistedLinkInfo
                {
                    Url = l.Url,
                    DisplayText = l.DisplayText,
                    Type = (int)l.Type,
                    ImportanceScore = l.ImportanceScore,
                    AriaLabel = l.AriaLabel,
                    ParentSelector = l.ParentSelector,
                    Author = l.Author,
                    PublishedDate = l.PublishedDate,
                    SectionTitle = l.SectionTitle,
                    IsFromImageAlt = l.IsFromImageAlt,
                    HeaderType = (int)l.HeaderType,
                }).ToList(),
            };

            if (buildCache.ReadableContent != null)
            {
                persisted.ReadableTitle = buildCache.ReadableContent.Title;
                persisted.ReadableAuthor = buildCache.ReadableContent.Author;
                persisted.ReadablePublishedDate = buildCache.ReadableContent.PublishedDate;
                persisted.ReadableCleanedText = buildCache.ReadableContent.CleanedText;
                persisted.ReadableParagraphs = buildCache.ReadableContent.Paragraphs.ToList();
                persisted.ReadableIsPaywalled = buildCache.ReadableContent.IsPaywalled;
            }

            if (buildCache.HierarchyConfig != null)
            {
                persisted.HierarchyDomain = buildCache.HierarchyConfig.Domain;
                persisted.HierarchyUrlPattern = buildCache.HierarchyConfig.UrlPattern;
                persisted.HierarchySections = buildCache.HierarchyConfig.Sections.Select(s => new PersistedHierarchySection
                {
                    Name = s.Name,
                    SortOrder = s.SortOrder,
                    ParentSelectors = s.ParentSelectors.ToList(),
                    UrlPatterns = s.UrlPatterns.ToList(),
                    StartCollapsed = s.StartCollapsed,
                }).ToList();
            }

            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            var filePath = GetBuildCacheFilePath(normalizedUrl);
            var tmpPath = filePath + ".tmp";

            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist build cache for {Url}", normalizedUrl);
        }
    }

    /// <summary>
    /// Loads all build cache entries from disk, filtering out expired entries.
    /// </summary>
    public Dictionary<string, PageBuildCache> LoadAllBuildCaches(int ttlSeconds = 57600)
    {
        var results = new Dictionary<string, PageBuildCache>();

        if (!Directory.Exists(_cacheDirectory))
        {
            return results;
        }

        foreach (var filePath in Directory.EnumerateFiles(_cacheDirectory, "*.build.json"))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var persisted = JsonSerializer.Deserialize<PersistedBuildCache>(json, JsonOptions);
                if (persisted == null)
                {
                    continue;
                }

                var links = persisted.Links.Select(l => new LinkInfo
                {
                    Url = l.Url,
                    DisplayText = l.DisplayText,
                    Type = (LinkType)l.Type,
                    ImportanceScore = l.ImportanceScore,
                    AriaLabel = l.AriaLabel,
                    ParentSelector = l.ParentSelector,
                    Author = l.Author,
                    PublishedDate = l.PublishedDate,
                    SectionTitle = l.SectionTitle,
                    IsFromImageAlt = l.IsFromImageAlt,
                    HeaderType = (HeaderType)l.HeaderType,
                }).ToList();

                var metadata = new PageMetadata
                {
                    Title = persisted.Title ?? string.Empty,
                    Description = persisted.MetaDescription,
                    CanonicalUrl = persisted.MetaCanonicalUrl,
                    Author = persisted.MetaAuthor,
                    PublishedDate = persisted.MetaPublishedDate,
                    FaviconUrl = persisted.MetaFaviconUrl,
                };

                SiteHierarchyConfig? hierarchyConfig = null;
                if (persisted.HierarchySections is { Count: > 0 })
                {
                    hierarchyConfig = new SiteHierarchyConfig
                    {
                        Domain = persisted.HierarchyDomain ?? string.Empty,
                        UrlPattern = persisted.HierarchyUrlPattern ?? string.Empty,
                        CreatedAt = persisted.CachedAt,
                        ModelVersion = "disk-cache",
                        Sections = persisted.HierarchySections.Select(s => new HierarchySection
                        {
                            Name = s.Name,
                            SortOrder = s.SortOrder,
                            ParentSelectors = s.ParentSelectors,
                            UrlPatterns = s.UrlPatterns,
                            StartCollapsed = s.StartCollapsed,
                        }).ToList(),
                    };
                }

                ReadableContent? readableContent = null;
                if (!string.IsNullOrEmpty(persisted.ReadableCleanedText)
                    && persisted.ReadableParagraphs is { Count: > 0 })
                {
                    readableContent = ReadableContent.Create(
                        persisted.ReadableTitle ?? metadata.Title ?? "Untitled",
                        persisted.ReadableCleanedText,
                        persisted.ReadableParagraphs,
                        persisted.ReadableAuthor,
                        persisted.ReadablePublishedDate,
                        persisted.ReadableIsPaywalled);
                }

                var buildCache = new PageBuildCache
                {
                    Links = links,
                    HierarchyConfig = hierarchyConfig,
                    ReadableContent = readableContent,
                    Metadata = metadata,
                    FinalUrl = persisted.FinalUrl ?? string.Empty,
                    Classification = (PageClassification)persisted.Classification,
                    ClassificationVersion = persisted.ClassificationVersion,
                    CachedAt = persisted.CachedAt,
                };

                // Skip expired build cache entries
                var age = DateTime.UtcNow - buildCache.CachedAt;
                if (age.TotalSeconds > ttlSeconds)
                {
                    _logger.LogDebug("Skipping expired build cache (age={AgeHours:F1}h): {Url}", age.TotalHours, persisted.FinalUrl);
                    File.Delete(filePath);
                    continue;
                }

                // Derive the normalized URL from the file name by looking up
                // the corresponding HTML cache entry, or use FinalUrl as key
                var key = UrlNormalizer.Normalize(persisted.FinalUrl ?? string.Empty);
                if (!string.IsNullOrEmpty(key))
                {
                    results[key] = buildCache;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load build cache from {Path}", filePath);
            }
        }

        _logger.LogInformation("Loaded {Count} build cache entries from disk", results.Count);
        return results;
    }

    /// <summary>
    /// Evicts oldest cache files (by last write time) until total disk usage
    /// is within <see cref="_maxDiskSizeBytes"/>. No-op when the limit is 0 (disabled).
    /// </summary>
    internal void EnforceSizeLimit()
    {
        if (_maxDiskSizeBytes <= 0 || !Directory.Exists(_cacheDirectory))
        {
            return;
        }

        try
        {
            var files = new DirectoryInfo(_cacheDirectory)
                .GetFiles("*.json")
                .Where(f => !f.Name.EndsWith(".build.json", StringComparison.OrdinalIgnoreCase))
                .Select(f => new { File = f, f.Length, f.LastWriteTimeUtc })
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var totalSize = files.Sum(f => f.Length);

            var evicted = 0;
            while (totalSize > _maxDiskSizeBytes && evicted < files.Count)
            {
                var oldest = files[evicted];
                try
                {
                    oldest.File.Delete();
                    totalSize -= oldest.Length;
                    evicted++;
                }
                catch
                {
                    // Best effort eviction; skip files that can't be deleted
                    evicted++;
                }
            }

            if (evicted > 0)
            {
                _logger.LogInformation(
                    "Disk cache eviction: removed {Count} oldest files, total size now {Size} bytes (limit: {Limit} bytes)",
                    evicted,
                    totalSize,
                    _maxDiskSizeBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enforce disk cache size limit");
        }
    }

    internal string GetFilePath(string normalizedUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
        var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".json";
        return Path.Combine(_cacheDirectory, fileName);
    }

    internal string GetBuildCacheFilePath(string normalizedUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
        var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".build.json";
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

    internal sealed class PersistedBuildCache
    {
        public string? FinalUrl { get; set; }

        public int Classification { get; set; }

        public int ClassificationVersion { get; set; }

        public DateTime CachedAt { get; set; }

        public string? Title { get; set; }

        public string? MetaDescription { get; set; }

        public string? MetaCanonicalUrl { get; set; }

        public string? MetaAuthor { get; set; }

        public DateTime? MetaPublishedDate { get; set; }

        public string? MetaFaviconUrl { get; set; }

        public List<PersistedLinkInfo> Links { get; set; } = [];

        public string? HierarchyDomain { get; set; }

        public string? HierarchyUrlPattern { get; set; }

        public List<PersistedHierarchySection>? HierarchySections { get; set; }

        // ReadableContent fields (article text for reader view)
        public string? ReadableTitle { get; set; }

        public string? ReadableAuthor { get; set; }

        public DateTime? ReadablePublishedDate { get; set; }

        public string? ReadableCleanedText { get; set; }

        public List<string>? ReadableParagraphs { get; set; }

        public bool ReadableIsPaywalled { get; set; }
    }

    internal sealed class PersistedLinkInfo
    {
        public string Url { get; set; } = string.Empty;

        public string DisplayText { get; set; } = string.Empty;

        public int Type { get; set; }

        public int ImportanceScore { get; set; }

        public string? AriaLabel { get; set; }

        public string? ParentSelector { get; set; }

        public string? Author { get; set; }

        public DateTime? PublishedDate { get; set; }

        public string? SectionTitle { get; set; }

        public bool IsFromImageAlt { get; set; }

        public int HeaderType { get; set; }
    }

    internal sealed class PersistedHierarchySection
    {
        public string Name { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public List<string> ParentSelectors { get; set; } = [];

        public List<string> UrlPatterns { get; set; } = [];

        public bool StartCollapsed { get; set; }
    }
}
