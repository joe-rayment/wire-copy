// Educational and personal use only.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Infrastructure.Browser.Cache;

namespace TermReader.Infrastructure.Podcast.Cache;

/// <summary>
/// Disk-based cache for extracted article content.
/// Stores all entries in a single JSON index file at {LocalAppData}/TermReader/article-cache/index.json.
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

            _logger.LogDebug(
                "Article content cache hit for {Url} ({Words} words)",
                url,
                entry.WordCount);

            return new ExtractedArticle
            {
                Title = entry.Title,
                CleanedText = entry.CleanedText,
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
            EnsureDirectoryExists();

            var now = DateTime.UtcNow;
            _index[key] = new CacheEntry
            {
                NormalizedUrl = key,
                Title = article.Title,
                CleanedText = article.CleanedText,
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
                _index.Remove(expired);
            }

            // If still over limit, evict oldest cached entries
            while (_index.Count > _maxEntries)
            {
                var oldest = _index.MinBy(kvp => kvp.Value.CachedAtUtc);
                if (oldest.Key != null)
                {
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
        EnsureDirectoryExists();

        var entries = _index.Values.ToList();
        var json = JsonSerializer.Serialize(entries, JsonOptions);

        var tmpPath = _indexPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, cancellationToken);
        File.Move(tmpPath, _indexPath, overwrite: true);
    }

    private void EnsureDirectoryExists()
    {
        Directory.CreateDirectory(_basePath);
    }

    private sealed class CacheEntry
    {
        public string NormalizedUrl { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string CleanedText { get; set; } = string.Empty;

        public string? Author { get; set; }

        public string Url { get; set; } = string.Empty;

        public int WordCount { get; set; }

        public DateTime? PublishedDate { get; set; }

        public DateTime CachedAtUtc { get; set; }
    }
}
