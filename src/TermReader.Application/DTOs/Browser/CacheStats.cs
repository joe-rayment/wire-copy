// Educational and personal use only.

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Statistics about the page cache.
/// </summary>
public record CacheStats
{
    /// <summary>
    /// Number of entries currently in the cache.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// Total size of cached content in bytes.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Maximum allowed cache size in bytes.
    /// </summary>
    public long MaxSizeBytes { get; init; }

    /// <summary>
    /// Number of cache hits since startup.
    /// </summary>
    public long HitCount { get; init; }

    /// <summary>
    /// Number of cache misses since startup.
    /// </summary>
    public long MissCount { get; init; }

    /// <summary>
    /// Number of articles in the persistent article content cache.
    /// </summary>
    public int ArticleCacheCount { get; init; }

    /// <summary>
    /// Number of files in the disk cache directory.
    /// </summary>
    public int DiskCacheFileCount { get; init; }

    /// <summary>
    /// Total size of disk cache files in bytes.
    /// </summary>
    public long DiskCacheSizeBytes { get; init; }

    /// <summary>
    /// Maximum disk cache size in bytes from configuration.
    /// </summary>
    public long MaxDiskSizeBytes { get; init; }

    /// <summary>
    /// Cache hit rate as a percentage (0-100).
    /// </summary>
    public double HitRatePercent => HitCount + MissCount > 0
        ? Math.Round((double)HitCount / (HitCount + MissCount) * 100, 1)
        : 0;

    /// <summary>
    /// Memory cache usage as a percentage (0-100).
    /// </summary>
    public double UsagePercent => MaxSizeBytes > 0
        ? Math.Round((double)TotalSizeBytes / MaxSizeBytes * 100, 1)
        : 0;

    /// <summary>
    /// Disk cache usage as a percentage (0-100).
    /// </summary>
    public double DiskUsagePercent => MaxDiskSizeBytes > 0
        ? Math.Round((double)DiskCacheSizeBytes / MaxDiskSizeBytes * 100, 1)
        : 0;

    /// <summary>
    /// Total size formatted as a human-readable string.
    /// </summary>
    public string FormattedSize => FormatBytes(TotalSizeBytes);

    /// <summary>
    /// Maximum size formatted as a human-readable string.
    /// </summary>
    public string FormattedMaxSize => FormatBytes(MaxSizeBytes);

    /// <summary>
    /// Disk cache size formatted as a human-readable string.
    /// </summary>
    public string FormattedDiskSize => FormatBytes(DiskCacheSizeBytes);

    /// <summary>
    /// Maximum disk size formatted as a human-readable string.
    /// </summary>
    public string FormattedMaxDiskSize => FormatBytes(MaxDiskSizeBytes);

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
        };
    }
}
