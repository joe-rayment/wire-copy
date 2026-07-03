// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser.Cache;

/// <summary>
/// In-memory page cache with LRU + TTL + size-cap eviction and optional disk persistence.
/// Thread-safe via ConcurrentDictionary and Interlocked operations.
/// </summary>
public sealed class InMemoryPageCache : IPageCache, IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, PageBuildCache> _standaloneBuildCache = new();
    private readonly ConcurrentDictionary<string, string> _normalizedUrlCache = new();
    private readonly CacheConfiguration _config;
    private readonly ILogger<InMemoryPageCache> _logger;
    private readonly DiskCacheStore? _diskStore;
    private readonly Timer _evictionTimer;

    private long _totalSizeBytes;
    private long _hitCount;
    private long _missCount;

    public InMemoryPageCache(
        IOptions<CacheConfiguration> config,
        ILogger<InMemoryPageCache> logger)
        : this(config, logger, diskStore: null)
    {
    }

    internal InMemoryPageCache(
        IOptions<CacheConfiguration> config,
        ILogger<InMemoryPageCache> logger,
        DiskCacheStore? diskStore)
    {
        _config = config.Value;
        _logger = logger;
        _diskStore = diskStore;

        LoadFromDisk();

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
        _logger.LogDebug(
            "Cache hit key={Key}, requestUrl={RequestUrl}, finalUrl={FinalUrl}",
            key,
            entry.Metadata.RequestUrl,
            entry.Metadata.FinalUrl);

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

        _logger.LogDebug(
            "Cached {Url} ({Size} bytes, total: {Total} bytes)",
            requestUrl,
            entrySize,
            Interlocked.Read(ref _totalSizeBytes));

        // Persist to disk asynchronously
        PersistToDisk(key, result, metadata);
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
        _standaloneBuildCache.Clear();
        _normalizedUrlCache.Clear();
        Interlocked.Exchange(ref _totalSizeBytes, 0);

        _diskStore?.ClearAll();

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
            MissCount = Interlocked.Read(ref _missCount),
            DiskCacheFileCount = _diskStore?.GetFileCount() ?? 0,
            DiskCacheSizeBytes = _diskStore?.GetTotalSizeBytes() ?? 0,
            MaxDiskSizeBytes = _diskStore?.MaxDiskSizeBytes ?? 0
        };
    }

    public IReadOnlySet<string> GetCachedUrls()
    {
        return _entries
            .Where(kvp => !kvp.Value.Metadata.IsExpired)
            .Select(kvp => kvp.Value.Metadata.RequestUrl)
            .ToHashSet();
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

    public PageBuildCache? TryGetBuildCache(string url)
    {
        var key = NormalizeUrl(url);

        // Check the HTML cache entry first (build cache stored alongside HTML)
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.Metadata.IsExpired)
            {
                _logger.LogInformation("BuildCache check: HTML entry expired for {Url} (key={Key})", url, key);
            }
            else if (entry.BuildCache != null)
            {
                _logger.LogInformation("BuildCache HIT (HTML entry) for {Url} (key={Key})", url, key);
                return entry.BuildCache;
            }
            else
            {
                _logger.LogInformation("BuildCache check: HTML entry exists but no BuildCache for {Url} (key={Key})", url, key);
            }
        }
        else
        {
            _logger.LogInformation("BuildCache check: no HTML entry for {Url} (key={Key})", url, key);
        }

        // Fallback: standalone build cache (survives HTML eviction from fallback retries)
        if (_standaloneBuildCache.TryGetValue(key, out var standalone))
        {
            // Check TTL — standalone build cache entries expire at the same rate as page cache
            var age = DateTime.UtcNow - standalone.CachedAt;
            if (age.TotalSeconds > _config.DefaultTtlSeconds)
            {
                _logger.LogInformation("BuildCache EXPIRED (standalone) for {Url} (age={AgeHours:F1}h, key={Key})", url, age.TotalHours, key);
                _standaloneBuildCache.TryRemove(key, out _);
            }
            else
            {
                _logger.LogInformation("BuildCache HIT (standalone) for {Url} (key={Key})", url, key);
                return standalone;
            }
        }

        _logger.LogInformation("BuildCache MISS for {Url} (key={Key})", url, key);
        return null;
    }

    public void PutBuildCache(string url, PageBuildCache buildCache)
    {
        var key = NormalizeUrl(url);

        // Always store in standalone dict (survives HTML cache removal by fallback retries)
        _standaloneBuildCache[key] = buildCache;
        _logger.LogInformation("BuildCache PUT (standalone) for {Url} (key={Key})", url, key);

        // Also attach to the HTML entry if it exists
        if (_entries.TryGetValue(key, out var existing) && !existing.Metadata.IsExpired)
        {
            var updated = existing with { BuildCache = buildCache };
            _entries.TryUpdate(key, updated, existing);
            _logger.LogInformation("BuildCache PUT (attached to HTML entry) for {Url} (key={Key})", url, key);
        }

        // Persist to disk so build cache survives app restarts
        ThreadPool.QueueUserWorkItem(_ => _diskStore?.WriteBuildCache(key, buildCache));
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
    }

    public void ApplyLinkListTtl(string url)
    {
        UpdateTtl(url, _config.LinkListTtlSeconds);
    }

    public void UpdateTtl(string url, int ttlSeconds)
    {
        var key = NormalizeUrl(url);

        if (!_entries.TryGetValue(key, out var existing) || existing.Metadata.IsExpired)
        {
            return;
        }

        // workspace-hv8n — anchor the expiry to the ORIGINAL cache time, not "now". Recomputing from
        // DateTime.UtcNow on every read/revisit made the TTL slide forward indefinitely: a section page reopened
        // within its window pushed ExpiresAtUtc to now+TTL each time, so frequently-visited pages NEVER expired
        // (and dragged their attached build cache + disk copy along). Anchoring to CachedAtUtc makes the TTL an
        // ABSOLUTE cap. Clamp so an apply can only SHRINK the lifetime, never extend it (e.g. the link-list TTL
        // applied over a longer default must not lengthen an entry).
        var anchored = existing.Metadata.CachedAtUtc.AddSeconds(ttlSeconds);
        var newExpiry = anchored < existing.Metadata.ExpiresAtUtc ? anchored : existing.Metadata.ExpiresAtUtc;
        var updatedMetadata = existing.Metadata with
        {
            ExpiresAtUtc = newExpiry,
        };
        var updated = existing with { Metadata = updatedMetadata };
        if (_entries.TryUpdate(key, updated, existing))
        {
            PersistToDisk(key, updated.Result, updatedMetadata);
        }
    }

    /// <summary>
    /// Test hook (workspace-9k27.16): expose an entry's absolute expiry so TTL
    /// tests can assert on timestamps instead of racing wall-clock sleeps.
    /// </summary>
    internal DateTime? GetExpiresAtUtc(string url)
    {
        return _entries.TryGetValue(NormalizeUrl(url), out var entry)
            ? entry.Metadata.ExpiresAtUtc
            : null;
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

            // Remove from disk
            _diskStore?.Delete(removed.Metadata.NormalizedUrl);

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

            // Evict stale standalone build cache entries
            var staleBuildKeys = _standaloneBuildCache
                .Where(kvp => (DateTime.UtcNow - kvp.Value.CachedAt).TotalSeconds > _config.DefaultTtlSeconds)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in staleBuildKeys)
            {
                if (_standaloneBuildCache.TryRemove(key, out _))
                {
                    _diskStore?.DeleteBuildCache(key);
                }
            }

            if (staleBuildKeys.Count > 0)
            {
                _logger.LogDebug("Eviction sweep removed {Count} stale standalone build cache entries", staleBuildKeys.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during eviction sweep");
        }
    }

    private void LoadFromDisk()
    {
        if (_diskStore == null)
        {
            return;
        }

        try
        {
            var entries = _diskStore.LoadAll();

            foreach (var (result, metadata) in entries)
            {
                var key = metadata.NormalizedUrl;
                var entry = new CacheEntry(result, metadata);

                if (_entries.TryAdd(key, entry))
                {
                    Interlocked.Add(ref _totalSizeBytes, metadata.SizeBytes);
                }
            }

            // Evict if loaded entries exceed limits
            EvictToFit(0);

            _logger.LogInformation(
                "Loaded {Count} page cache entries from disk ({Size} bytes)",
                _entries.Count,
                Interlocked.Read(ref _totalSizeBytes));

            // Load persisted build caches into standalone dict
            var buildCaches = _diskStore.LoadAllBuildCaches(_config.DefaultTtlSeconds);
            foreach (var (key, buildCache) in buildCaches)
            {
                _standaloneBuildCache[key] = buildCache;

                // Attach to HTML entry if one exists
                if (_entries.TryGetValue(key, out var existing) && !existing.Metadata.IsExpired)
                {
                    var updated = existing with { BuildCache = buildCache };
                    _entries.TryUpdate(key, updated, existing);
                }
            }

            if (buildCaches.Count > 0)
            {
                _logger.LogInformation("Loaded {Count} build cache entries from disk", buildCaches.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load page cache from disk, starting fresh");
        }
    }

    private void PersistToDisk(string normalizedUrl, PageLoadResult result, CacheEntryMetadata metadata)
    {
        if (_diskStore == null)
        {
            return;
        }

        // Snapshot metadata to avoid data race with LastAccessedAtUtc updates on TryGet
        var metadataSnapshot = metadata with { };
        ThreadPool.QueueUserWorkItem(_ => _diskStore.Write(normalizedUrl, result, metadataSnapshot));
    }

    private sealed record CacheEntry(PageLoadResult Result, CacheEntryMetadata Metadata, PageBuildCache? BuildCache = null);
}
