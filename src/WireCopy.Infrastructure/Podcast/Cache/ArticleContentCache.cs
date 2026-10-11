// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WireCopy.Infrastructure.Browser.Cache;

namespace WireCopy.Infrastructure.Podcast.Cache;

/// <summary>
/// Disk-based cache for extracted article content.
/// Stores metadata in {basePath}/index.json and article text in {basePath}/articles/{hash}.txt.
/// Thread-safe via SemaphoreSlim. Atomic writes via .tmp-then-rename.
/// Safe to share between processes (the app and the browser-extension native host):
/// the in-memory index is reloaded whenever index.json changes on disk, and every
/// mutation holds an exclusive cross-process lock on {basePath}/index.lock while it
/// re-reads, modifies and rewrites the index.
/// </summary>
internal sealed class ArticleContentCache : IArticleContentCache
{
    private const int DefaultMaxEntries = 1000;
    private const string LockFileName = "index.lock";
    private const int LockInitialDelayMs = 25;
    private const int LockMaxDelayMs = 200;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(16);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _basePath;
    private readonly string _articlesDir;
    private readonly string _indexPath;
    private readonly string _lockPath;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly ILogger<ArticleContentCache> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, CacheEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private IndexStamp? _indexStamp;

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
        _lockPath = Path.Combine(basePath, LockFileName);
        _ttl = ttl;
        _maxEntries = maxEntries;
        _logger = logger;
    }

    public async Task<ExtractedArticle?> TryGetAsync(string url, CancellationToken cancellationToken = default)
    {
        var key = UrlNormalizer.Normalize(url);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);

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
                    await RemoveStaleEntryAsync(key, entry, cancellationToken).ConfigureAwait(false);
                    return null;
                }

                cleanedText = await File.ReadAllTextAsync(entry.ArticleFilePath, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(cleanedText))
            {
                _logger.LogWarning("No article text available for {Url}, removing entry", url);
                await RemoveStaleEntryAsync(key, entry, cancellationToken).ConfigureAwait(false);
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

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectoriesExist();
            await using var fileLock = await AcquireFileLockAndReloadAsync(cancellationToken).ConfigureAwait(false);

            // Delete old article file if overwriting an existing entry
            if (_index.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing.ArticleFilePath))
            {
                TryDeleteFile(existing.ArticleFilePath);
            }

            // Write article text to a separate file
            var articleFileName = ComputeUrlHash(key) + ".txt";
            var articleFilePath = Path.Combine(_articlesDir, articleFileName);

            var tmpArticlePath = articleFilePath + TmpSuffix();
            await File.WriteAllTextAsync(tmpArticlePath, article.CleanedText, cancellationToken).ConfigureAwait(false);
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

            await SaveIndexAsync(cancellationToken).ConfigureAwait(false);

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

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAndReloadAsync(cancellationToken).ConfigureAwait(false);

            if (_index.TryGetValue(key, out var entry))
            {
                if (entry.ArticleFilePath != null)
                {
                    TryDeleteFile(entry.ArticleFilePath);
                }

                _index.Remove(key);
                await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
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
        return Path.Combine(appData, "WireCopy", "article-cache");
    }

    /// <summary>
    /// Per-process suffix for temp files, so two processes writing the same
    /// target never collide on a shared ".tmp" name.
    /// </summary>
    private static string TmpSuffix() =>
        "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";

    /// <summary>
    /// Reloads the in-memory index when index.json changed on disk (or was never loaded).
    /// Used by read paths, which do not take the cross-process lock.
    /// </summary>
    private async Task EnsureIndexCurrentAsync(CancellationToken cancellationToken)
    {
        if (_indexStamp.HasValue && _indexStamp.Value == ReadIndexStamp())
        {
            return;
        }

        await ReloadIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads index.json from disk. The stamp is captured before the read, so a write
    /// racing with the read at worst triggers one extra reload next time.
    /// If the read fails after an earlier successful load, the old in-memory index is kept
    /// (and the stamp left stale so the next call retries).
    /// </summary>
    private async Task ReloadIndexAsync(CancellationToken cancellationToken)
    {
        var stamp = ReadIndexStamp();
        var hadPriorLoad = _indexStamp.HasValue;

        if (!stamp.Exists)
        {
            _index = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            _indexStamp = stamp;
            return;
        }

        try
        {
            // FileShare.Delete lets another process atomically replace the file while we read.
            await using var stream = new FileStream(
                _indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            var entries = await JsonSerializer
                .DeserializeAsync<List<CacheEntry>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            _index = (entries ?? []).ToDictionary(
                e => e.NormalizedUrl,
                StringComparer.OrdinalIgnoreCase);
            _indexStamp = stamp;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (hadPriorLoad)
            {
                _logger.LogWarning(ex, "Failed to reload article content cache index, keeping the in-memory copy");
                return;
            }

            _logger.LogWarning(ex, "Failed to load article content cache index, starting fresh");
            _index = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            _indexStamp = stamp;
        }
    }

    /// <summary>
    /// Takes the cross-process lock, then ALWAYS re-reads the index from disk so the caller
    /// mutates the latest state. Dispose the returned stream to release the lock. The lock
    /// file itself is never deleted.
    /// </summary>
    private async Task<FileStream> AcquireFileLockAndReloadAsync(CancellationToken cancellationToken)
    {
        var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReloadIndexAsync(cancellationToken).ConfigureAwait(false);
            return fileLock;
        }
        catch
        {
            await fileLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_basePath);

        var stopwatch = Stopwatch.StartNew();
        var delayMs = LockInitialDelayMs;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                if (stopwatch.Elapsed >= LockTimeout)
                {
                    throw new TimeoutException(
                        $"Timed out after {LockTimeout.TotalSeconds:0}s waiting for the article cache lock: {_lockPath}",
                        ex);
                }

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, LockMaxDelayMs);
            }
        }
    }

    /// <summary>
    /// Removes an entry found to be unusable, but only if the on-disk entry is still the
    /// same one we judged stale (another process may have replaced it in the meantime).
    /// </summary>
    private async Task RemoveStaleEntryAsync(string key, CacheEntry stale, CancellationToken cancellationToken)
    {
        await using var fileLock = await AcquireFileLockAndReloadAsync(cancellationToken).ConfigureAwait(false);

        if (_index.TryGetValue(key, out var current)
            && current.CachedAtUtc == stale.CachedAtUtc
            && string.Equals(current.ArticleFilePath, stale.ArticleFilePath, StringComparison.Ordinal))
        {
            _index.Remove(key);
            await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private IndexStamp ReadIndexStamp()
    {
        var info = new FileInfo(_indexPath);
        return info.Exists
            ? new IndexStamp(true, info.LastWriteTimeUtc, info.Length)
            : new IndexStamp(false, default, 0);
    }

    private async Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoriesExist();

        var entries = _index.Values.ToList();
        var json = JsonSerializer.Serialize(entries, JsonOptions);

        var tmpPath = _indexPath + TmpSuffix();
        await File.WriteAllTextAsync(tmpPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tmpPath, _indexPath, overwrite: true);
        _indexStamp = ReadIndexStamp();
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

    /// <summary>Cheap fingerprint of index.json used to detect writes by another process.</summary>
    private readonly record struct IndexStamp(bool Exists, DateTime LastWriteUtc, long Length);

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
