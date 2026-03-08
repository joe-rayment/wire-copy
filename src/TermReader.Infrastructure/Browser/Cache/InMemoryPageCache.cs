// Educational and personal use only.

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser.Cache;

/// <summary>
/// In-memory page cache with LRU + TTL + size-cap eviction.
/// Thread-safe via ConcurrentDictionary and Interlocked operations.
/// </summary>
public sealed class InMemoryPageCache : IPageCache, IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, string> _normalizedUrlCache = new();
    private readonly CacheConfiguration _config;
    private readonly ILogger<InMemoryPageCache> _logger;
    private readonly Timer _evictionTimer;

    private long _totalSizeBytes;
    private long _hitCount;
    private long _missCount;

    public InMemoryPageCache(
        IOptions<CacheConfiguration> config,
        ILogger<InMemoryPageCache> logger)
    {
        _config = config.Value;
        _logger = logger;

        _evictionTimer = new Timer(
            EvictionSweep,
            null,
            TimeSpan.FromSeconds(_config.EvictionSweepIntervalSeconds),
            TimeSpan.FromSeconds(_config.EvictionSweepIntervalSeconds));
    }

    public PageLoadResult? TryGet(string url)
    {
        var key = NormalizeUrl(url);

        if (!_entries.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref _missCount);
            return null;
        }

        if (entry.Metadata.IsExpired)
        {
            RemoveEntry(key);
            Interlocked.Increment(ref _missCount);
            return null;
        }

        // Update last accessed time for LRU
        entry.Metadata.LastAccessedAtUtc = DateTime.UtcNow;
        Interlocked.Increment(ref _hitCount);

        _logger.LogDebug(
            "Cache hit for {Url} (cached {Age} ago)",
            url,
            DateTime.UtcNow - entry.Metadata.CachedAtUtc);

        return entry.Result;
    }

    public void Put(string requestUrl, PageLoadResult result)
    {
        if (!result.Success)
        {
            return;
        }

        var key = NormalizeUrl(requestUrl);
        var entrySize = EstimateSize(result);

        if (entrySize > _config.MaxEntrySizeBytes)
        {
            _logger.LogWarning(
                "Skipping cache for {Url}: entry size {Size} exceeds max {Max}",
                requestUrl,
                entrySize,
                _config.MaxEntrySizeBytes);
            return;
        }

        // Evict entries to make room
        EvictToFit(entrySize);

        var metadata = new CacheEntryMetadata
        {
            RequestUrl = requestUrl,
            FinalUrl = result.Url,
            NormalizedUrl = key,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(_config.DefaultTtlSeconds),
            SizeBytes = entrySize
        };

        var entry = new CacheEntry(result, metadata);

        if (_entries.TryGetValue(key, out var existing))
        {
            // Replacing existing entry: subtract old size first
            if (_entries.TryUpdate(key, entry, existing))
            {
                Interlocked.Add(ref _totalSizeBytes, entrySize - existing.Metadata.SizeBytes);
            }
        }
        else
        {
            if (_entries.TryAdd(key, entry))
            {
                Interlocked.Add(ref _totalSizeBytes, entrySize);
            }
        }

        // Also index by final URL if different from request URL
        var finalKey = NormalizeUrl(result.Url);
        if (!string.Equals(key, finalKey, StringComparison.Ordinal))
        {
            _entries.TryAdd(finalKey, entry);
        }

        _logger.LogDebug(
            "Cached {Url} ({Size} bytes, total: {Total} bytes)",
            requestUrl,
            entrySize,
            Interlocked.Read(ref _totalSizeBytes));
    }

    public bool Remove(string url)
    {
        var key = NormalizeUrl(url);
        return RemoveEntry(key);
    }

    public bool Contains(string url)
    {
        var key = NormalizeUrl(url);
        return _entries.TryGetValue(key, out var entry) && !entry.Metadata.IsExpired;
    }

    public CacheStats Clear()
    {
        var stats = GetStats();
        _entries.Clear();
        _normalizedUrlCache.Clear();
        Interlocked.Exchange(ref _totalSizeBytes, 0);
        _logger.LogInformation(
            "Cache cleared: {Count} entries, {Size} bytes freed",
            stats.EntryCount,
            stats.TotalSizeBytes);
        return stats;
    }

    public CacheStats GetStats()
    {
        return new CacheStats
        {
            EntryCount = _entries.Count,
            TotalSizeBytes = Interlocked.Read(ref _totalSizeBytes),
            MaxSizeBytes = _config.MaxSizeBytes,
            HitCount = Interlocked.Read(ref _hitCount),
            MissCount = Interlocked.Read(ref _missCount)
        };
    }

    public IReadOnlySet<string> GetCachedUrls()
    {
        var urls = new HashSet<string>();
        foreach (var kvp in _entries)
        {
            if (kvp.Value.Metadata.IsExpired)
            {
                continue;
            }

            urls.Add(kvp.Value.Metadata.RequestUrl);
            urls.Add(kvp.Value.Metadata.FinalUrl);
        }

        return urls;
    }

    public DateTime? GetCachedAt(string url)
    {
        var key = NormalizeUrl(url);
        if (_entries.TryGetValue(key, out var entry) && !entry.Metadata.IsExpired)
        {
            return entry.Metadata.CachedAtUtc;
        }

        return null;
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
    }

    private static long EstimateSize(PageLoadResult result)
    {
        var htmlBytes = !string.IsNullOrEmpty(result.Html)
            ? Encoding.UTF8.GetByteCount(result.Html)
            : 0L;

        // 1KB overhead for metadata and object references
        return htmlBytes + 1024;
    }

    private string NormalizeUrl(string url)
    {
        return _normalizedUrlCache.GetOrAdd(url, UrlNormalizer.Normalize);
    }

    private bool RemoveEntry(string key)
    {
        if (_entries.TryRemove(key, out var removed))
        {
            Interlocked.Add(ref _totalSizeBytes, -removed.Metadata.SizeBytes);

            // Clean up alias key (same entry indexed under both request and final URL)
            var normalizedUrl = removed.Metadata.NormalizedUrl;
            var finalKey = NormalizeUrl(removed.Metadata.FinalUrl);
            var aliasKey = string.Equals(key, normalizedUrl, StringComparison.Ordinal)
                ? finalKey
                : normalizedUrl;

            if (!string.Equals(key, aliasKey, StringComparison.Ordinal) &&
                _entries.TryGetValue(aliasKey, out var aliasEntry) &&
                ReferenceEquals(aliasEntry, removed))
            {
                _entries.TryRemove(aliasKey, out _);
            }

            return true;
        }

        return false;
    }

    private void EvictToFit(long requiredBytes)
    {
        // Evict expired entries first
        EvictExpired();

        // Evict LRU entries if still over size limit
        while (Interlocked.Read(ref _totalSizeBytes) + requiredBytes > _config.MaxSizeBytes ||
               _entries.Count >= _config.MaxEntries)
        {
            var lruKey = FindLruKey();
            if (lruKey == null)
            {
                break;
            }

            if (_entries.TryGetValue(lruKey, out var lruEntry))
            {
                _logger.LogDebug("Evicting LRU cache entry: {Url}", lruEntry.Metadata.RequestUrl);
            }

            RemoveEntry(lruKey);
        }
    }

    private string? FindLruKey()
    {
        string? oldestKey = null;
        var oldestTime = DateTime.MaxValue;

        foreach (var kvp in _entries)
        {
            var accessed = kvp.Value.Metadata.LastAccessedAtUtc;
            if (accessed < oldestTime)
            {
                oldestTime = accessed;
                oldestKey = kvp.Key;
            }
        }

        return oldestKey;
    }

    private void EvictExpired()
    {
        var expiredKeys = _entries
            .Where(e => e.Value.Metadata.IsExpired)
            .Select(e => e.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _logger.LogDebug("Evicting expired cache entry: {Key}", key);
            RemoveEntry(key);
        }
    }

    private void EvictionSweep(object? state)
    {
        try
        {
            var countBefore = _entries.Count;
            EvictExpired();
            var evicted = countBefore - _entries.Count;
            if (evicted > 0)
            {
                _logger.LogDebug("Eviction sweep removed {Count} expired entries", evicted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during eviction sweep");
        }
    }

    private sealed record CacheEntry(PageLoadResult Result, CacheEntryMetadata Metadata);
}
