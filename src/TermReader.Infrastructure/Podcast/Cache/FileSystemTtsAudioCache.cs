// Educational and personal use only.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Podcast.Cache;

/// <summary>
/// Disk-based cache for TTS-generated audio files.
/// Stores audio in {BasePath}/audio/ with a JSON index at {BasePath}/index.json.
/// Thread-safe via SemaphoreSlim. Atomic writes via .tmp-then-rename.
/// </summary>
public class FileSystemTtsAudioCache : ITtsAudioCache
{
    private const int ContentHashLength = 16;
    private const int ConfigHashLength = 8;
    private const decimal CostPerMillionChars = 15.0m;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _basePath;
    private readonly string _audioDir;
    private readonly string _indexPath;
    private readonly long _maxSizeBytes;
    private readonly TimeSpan _ttl;
    private readonly string _ttsConfigHash;
    private readonly ILogger<FileSystemTtsAudioCache> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, CacheIndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexLoaded;

    public FileSystemTtsAudioCache(
        IOptions<TtsAudioCacheConfiguration> cacheConfig,
        IOptions<OpenAiTtsConfiguration> ttsConfig,
        ILogger<FileSystemTtsAudioCache> logger)
    {
        var config = cacheConfig.Value;
        _basePath = config.GetEffectiveBasePath();
        _audioDir = Path.Combine(_basePath, "audio");
        _indexPath = Path.Combine(_basePath, "index.json");
        _maxSizeBytes = config.MaxSizeBytes;
        _ttl = config.Ttl;
        _logger = logger;

        var tts = ttsConfig.Value;
        _ttsConfigHash = ComputeHash($"{tts.Voice}|{tts.Model}|{tts.Speed}|{tts.OutputFormat}")[..ConfigHashLength];
    }

    public async Task<TtsAudioCacheEntry?> TryGetAsync(string articleText, string url, CancellationToken cancellationToken = default)
    {
        var contentHash = ComputeHash(articleText)[..ContentHashLength];
        var cacheKey = $"{contentHash}_{_ttsConfigHash}";

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);

            if (!_index.TryGetValue(cacheKey, out var entry))
            {
                _logger.LogDebug("Cache miss for {Url} (key={Key})", url, cacheKey);
                return null;
            }

            // Check TTL
            if (DateTime.UtcNow - entry.CachedAtUtc > _ttl)
            {
                _logger.LogDebug("Cache entry expired for {Url} (key={Key})", url, cacheKey);
                _index.Remove(cacheKey);
                await SaveIndexAsync(cancellationToken);
                TryDeleteFile(entry.AudioFilePath);
                return null;
            }

            // Verify file exists
            if (!File.Exists(entry.AudioFilePath))
            {
                _logger.LogWarning("Cache file missing for {Url}: {Path}", url, entry.AudioFilePath);
                _index.Remove(cacheKey);
                await SaveIndexAsync(cancellationToken);
                return null;
            }

            // Update last accessed time (LRU tracking)
            entry.LastAccessedAtUtc = DateTime.UtcNow;
            await SaveIndexAsync(cancellationToken);

            _logger.LogInformation("Cache hit for {Url} (key={Key}, size={Size})", url, cacheKey, entry.FileSizeBytes);

            return new TtsAudioCacheEntry
            {
                CacheKey = cacheKey,
                AudioFilePath = entry.AudioFilePath,
                FileSizeBytes = entry.FileSizeBytes,
                CachedAtUtc = entry.CachedAtUtc,
                ContentHash = contentHash,
                TtsConfigHash = _ttsConfigHash,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve cached audio for {Url}", url);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TtsAudioCacheEntry> PutAsync(string articleText, string url, string title, byte[] audioData, CancellationToken cancellationToken = default)
    {
        var contentHash = ComputeHash(articleText)[..ContentHashLength];
        var cacheKey = $"{contentHash}_{_ttsConfigHash}";

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);
            EnsureDirectoriesExist();

            var audioFileName = $"{cacheKey}.aac";
            var audioFilePath = Path.Combine(_audioDir, audioFileName);

            // Atomic write: write to .tmp then rename
            var tmpPath = audioFilePath + ".tmp";
            await File.WriteAllBytesAsync(tmpPath, audioData, cancellationToken);
            File.Move(tmpPath, audioFilePath, overwrite: true);

            var entry = new CacheIndexEntry
            {
                CacheKey = cacheKey,
                AudioFilePath = audioFilePath,
                FileSizeBytes = audioData.Length,
                CachedAtUtc = DateTime.UtcNow,
                LastAccessedAtUtc = DateTime.UtcNow,
                ContentHash = contentHash,
                TtsConfigHash = _ttsConfigHash,
                Url = url,
                Title = title,
            };

            _index[cacheKey] = entry;
            await SaveIndexAsync(cancellationToken);

            _logger.LogInformation("Cached audio for {Url} (key={Key}, size={Size})", url, cacheKey, audioData.Length);

            return new TtsAudioCacheEntry
            {
                CacheKey = cacheKey,
                AudioFilePath = audioFilePath,
                FileSizeBytes = audioData.Length,
                CachedAtUtc = entry.CachedAtUtc,
                ContentHash = contentHash,
                TtsConfigHash = _ttsConfigHash,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CacheAnalysis> AnalyzeCollectionAsync(IReadOnlyList<(string Url, string Title, string Text)> articles, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);

            var statuses = new List<ArticleCacheStatus>();
            var cachedCount = 0;

            foreach (var (url, title, text) in articles)
            {
                var contentHash = ComputeHash(text)[..ContentHashLength];
                var cacheKey = $"{contentHash}_{_ttsConfigHash}";
                var isCached = _index.ContainsKey(cacheKey);
                var cost = isCached ? 0m : EstimateCost(text);

                if (isCached)
                {
                    cachedCount++;
                }

                statuses.Add(new ArticleCacheStatus
                {
                    Url = url,
                    Title = title,
                    IsCached = isCached,
                    EstimatedCost = cost,
                });
            }

            return new CacheAnalysis
            {
                TotalArticles = articles.Count,
                CachedArticles = cachedCount,
                UncachedArticles = articles.Count - cachedCount,
                EstimatedCost = statuses.Sum(s => s.EstimatedCost),
                ArticleStatuses = statuses,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze collection cache status");
            return new CacheAnalysis
            {
                TotalArticles = articles.Count,
                CachedArticles = 0,
                UncachedArticles = articles.Count,
                EstimatedCost = 0,
                ArticleStatuses = [],
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TtsCacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);

            return new TtsCacheStats
            {
                EntryCount = _index.Count,
                TotalSizeBytes = _index.Values.Sum(e => e.FileSizeBytes),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cache stats");
            return new TtsCacheStats { EntryCount = 0, TotalSizeBytes = 0 };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EvictAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var expiredKeys = _index
                .Where(kvp => now - kvp.Value.CachedAtUtc > _ttl)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                TryDeleteFile(_index[key].AudioFilePath);
                _index.Remove(key);
            }

            _logger.LogInformation("Evicted {Count} expired entries", expiredKeys.Count);

            // LRU eviction if still over size limit
            var totalSize = _index.Values.Sum(e => e.FileSizeBytes);
            if (totalSize > _maxSizeBytes)
            {
                var sortedByAccess = _index
                    .OrderBy(kvp => kvp.Value.LastAccessedAtUtc)
                    .ToList();

                var evictedLru = 0;
                foreach (var kvp in sortedByAccess)
                {
                    if (totalSize <= _maxSizeBytes)
                    {
                        break;
                    }

                    totalSize -= kvp.Value.FileSizeBytes;
                    TryDeleteFile(kvp.Value.AudioFilePath);
                    _index.Remove(kvp.Key);
                    evictedLru++;
                }

                _logger.LogInformation("Evicted {Count} LRU entries to meet size limit", evictedLru);
            }

            await SaveIndexAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evict cache entries");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureIndexLoadedAsync(cancellationToken);

            foreach (var entry in _index.Values)
            {
                TryDeleteFile(entry.AudioFilePath);
            }

            _index.Clear();
            await SaveIndexAsync(cancellationToken);

            _logger.LogInformation("Cleared all cache entries");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache");
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string ComputeHash(string input)
    {
        // Use IncrementalHash to avoid allocating a byte[] for the entire input.
        // Process the string in chunks to bound memory usage for large articles.
        const int chunkSize = 4096;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        var charSpan = input.AsSpan();
        Span<byte> buffer = stackalloc byte[chunkSize * 4]; // max 4 bytes per UTF-8 char

        while (charSpan.Length > 0)
        {
            var charsToProcess = Math.Min(charSpan.Length, chunkSize);
            var chunk = charSpan[..charsToProcess];
            var isFinal = charsToProcess == charSpan.Length;
            var bytesWritten = encoder.GetBytes(chunk, buffer, isFinal);
            hash.AppendData(buffer[..bytesWritten]);
            charSpan = charSpan[charsToProcess..];
        }

        Span<byte> hashBytes = stackalloc byte[32];
        hash.GetHashAndReset(hashBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static decimal EstimateCost(string text)
    {
        return text.Length * CostPerMillionChars / 1_000_000m;
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
                var entries = JsonSerializer.Deserialize<List<CacheIndexEntry>>(json, JsonOptions);
                if (entries != null)
                {
                    _index = entries.ToDictionary(e => e.CacheKey, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cache index from {Path}, starting fresh", _indexPath);
                _index = new Dictionary<string, CacheIndexEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        _indexLoaded = true;
    }

    private async Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoriesExist();

        var entries = _index.Values.ToList();
        var json = JsonSerializer.Serialize(entries, JsonOptions);

        // Atomic write
        var tmpPath = _indexPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, cancellationToken);
        File.Move(tmpPath, _indexPath, overwrite: true);
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(_audioDir);
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
            _logger.LogWarning(ex, "Failed to delete cache file: {Path}", path);
        }
    }

    /// <summary>
    /// Internal index entry with additional metadata not exposed in the public DTO.
    /// </summary>
    private sealed class CacheIndexEntry
    {
        public string CacheKey { get; set; } = string.Empty;

        public string AudioFilePath { get; set; } = string.Empty;

        public long FileSizeBytes { get; set; }

        public DateTime CachedAtUtc { get; set; }

        public DateTime LastAccessedAtUtc { get; set; }

        public string ContentHash { get; set; } = string.Empty;

        public string TtsConfigHash { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
    }
}
