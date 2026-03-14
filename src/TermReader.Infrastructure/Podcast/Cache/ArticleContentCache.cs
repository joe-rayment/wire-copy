// Educational and personal use only.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Infrastructure.Browser.Cache;

namespace TermReader.Infrastructure.Podcast.Cache;

/// <summary>
/// Disk-based cache for extracted article content.
/// Stores metadata in {basePath}/index.json and article text in {basePath}/articles/{hash}.txt.
/// Thread-safe via SemaphoreSlim. Atomic writes via .tmp-then-rename.
/// </summary>
internal sealed class ArticleContentCache : IArticleContentCache
{
    private const int DefaultMaxEntries = 1000;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _basePath;
    private readonly string _articlesDir;
    private readonly string _indexPath;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly ILogger<ArticleContentCache> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, CacheEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexLoaded;

    public ArticleContentCache(ILogger<ArticleContentCache> logger)
        : this(GetDefaultBasePath(), DefaultTtl, DefaultMaxEntries, logger)
    {
    }

    internal ArticleContentCache(
        string basePath,
        TimeSpan ttl,
        int maxEntries,
        ILogger<ArticleContentCache> logger)
    {
        _basePath = basePath;
        _articlesDir = Path.Combine(basePath, "articles");
        _indexPath = Path.Combine(basePath, "index.json");
        _ttl = ttl;
        _maxEntries = maxEntries;
        _logger = logger;
    }

    public async Task<ExtractedArticle?> TryGetAsync(string url, CancellationToken cancellationToken = default)
    {
        var key = UrlNormalizer.Normalize(url);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);

            if (!_index.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (DateTime.UtcNow - entry.CachedAtUtc > _ttl)
            {
                _logger.LogDebug("Article content cache entry expired for {Url}", url);
                return null;
            }

            // Backward compat: serve inline text from old-format entries
            var cleanedText = entry.CleanedText;

            if (string.IsNullOrEmpty(cleanedText) && !string.IsNullOrEmpty(entry.ArticleFilePath))
            {
                if (!File.Exists(entry.ArticleFilePath))
                {
                    _logger.LogWarning("Article file missing for {Url}: {Path}, removing entry", url, entry.ArticleFilePath);
                    _index.Remove(key);
                    await SaveIndexAsync(cancellationToken);
                    return null;
                }

                cleanedText = await File.ReadAllTextAsync(entry.ArticleFilePath, cancellationToken);
            }

            if (string.IsNullOrEmpty(cleanedText))
            {
                _logger.LogWarning("No article text available for {Url}, removing entry", url);
                _index.Remove(key);
                await SaveIndexAsync(cancellationToken);
                return null;
            }

            _logger.LogDebug(
                "Article content cache hit for {Url} ({Words} words)",
                url,
                entry.WordCount);

            return new ExtractedArticle
            {
                Title = entry.Title,
                CleanedText = cleanedText,
                Author = entry.Author,
                Url = entry.Url,
                WordCount = entry.WordCount,
                PublishedDate = entry.PublishedDate,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PutAsync(string url, ExtractedArticle article, CancellationToken cancellationToken = default)
    {
        var key = UrlNormalizer.Normalize(url);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);
            EnsureDirectoriesExist();

            // Delete old article file if overwriting an existing entry
            if (_index.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing.ArticleFilePath))
            {
                TryDeleteFile(existing.ArticleFilePath);
            }

            // Write article text to a separate file
            var articleFileName = ComputeUrlHash(key) + ".txt";
            var articleFilePath = Path.Combine(_articlesDir, articleFileName);

            var tmpArticlePath = articleFilePath + ".tmp";
            await File.WriteAllTextAsync(tmpArticlePath, article.CleanedText, cancellationToken);
            File.Move(tmpArticlePath, articleFilePath, overwrite: true);

            var now = DateTime.UtcNow;
            _index[key] = new CacheEntry
            {
                NormalizedUrl = key,
                Title = article.Title,
                ArticleFilePath = articleFilePath,
                Author = article.Author,
                Url = article.Url,
                WordCount = article.WordCount,
                PublishedDate = article.PublishedDate,
                CachedAtUtc = now,
            };

            // Evict expired entries first
            var expiredKeys = _index
                .Where(kvp => now - kvp.Value.CachedAtUtc > _ttl)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var expired in expiredKeys)
            {
                DeleteArticleFile(_index[expired]);
                _index.Remove(expired);
            }

            // If still over limit, evict oldest cached entries
            while (_index.Count > _maxEntries)
            {
                var oldest = _index.MinBy(kvp => kvp.Value.CachedAtUtc);
                if (oldest.Key != null)
                {
                    DeleteArticleFile(oldest.Value);
                    _index.Remove(oldest.Key);
                }
            }

            await SaveIndexAsync(cancellationToken);

            _logger.LogDebug(
                "Cached article content for {Url} ({Words} words)",
                url,
                article.WordCount);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string url, CancellationToken cancellationToken = default)
    {
        var key = UrlNormalizer.Normalize(url);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_index.TryGetValue(key, out var entry))
            {
                TryDeleteFile(entry.ArticleFilePath);
                _index.Remove(key);
                await SaveIndexAsync(cancellationToken);
                _logger.LogDebug("Removed cached article: {Url}", url);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static string ComputeUrlHash(string normalizedUrl)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetDefaultBasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "TermReader", "article-cache");
    }

    private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
    {
        if (_indexLoaded)
        {
            return;
        }

        if (File.Exists(_indexPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_indexPath, cancellationToken);
                var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json, JsonOptions);
                if (entries != null)
                {
                    _index = entries.ToDictionary(
                        e => e.NormalizedUrl,
                        StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load article content cache index, starting fresh");
                _index = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        _indexLoaded = true;
    }

    private async Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoriesExist();

        var entries = _index.Values.ToList();
        var json = JsonSerializer.Serialize(entries, JsonOptions);

        var tmpPath = _indexPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, cancellationToken);
        File.Move(tmpPath, _indexPath, overwrite: true);
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(_articlesDir);
    }

    private void DeleteArticleFile(CacheEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ArticleFilePath))
        {
            TryDeleteFile(entry.ArticleFilePath);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete article file: {Path}", path);
        }
    }

    private sealed class CacheEntry
    {
        public string NormalizedUrl { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Path to the file containing the article text.
        /// New-format entries use this instead of inline CleanedText.
        /// </summary>
        public string? ArticleFilePath { get; set; }

        /// <summary>
        /// Legacy field: inline article text from old cache format.
        /// Retained for backward compatibility during deserialization.
        /// New entries do not populate this field.
        /// </summary>
#pragma warning disable S3459, S1144 // Needed for JSON deserialization of old-format entries
        public string? CleanedText { get; set; }
#pragma warning restore S3459, S1144

        public string? Author { get; set; }

        public string Url { get; set; } = string.Empty;

        public int WordCount { get; set; }

        public DateTime? PublishedDate { get; set; }

        public DateTime CachedAtUtc { get; set; }
    }
}
