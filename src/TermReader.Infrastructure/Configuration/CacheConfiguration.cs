// Educational and personal use only.

namespace TermReader.Infrastructure.Configuration;

public class CacheConfiguration
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Maximum cache size in bytes. Default: 100 MB.
    /// </summary>
    public long MaxSizeBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum number of cache entries. Default: 200.
    /// </summary>
    public int MaxEntries { get; init; } = 200;

    /// <summary>
    /// Default time-to-live for cache entries in seconds. Default: 24 hours.
    /// </summary>
    public int DefaultTtlSeconds { get; init; } = 86400;

    /// <summary>
    /// Maximum size of a single cache entry in bytes. Default: 5 MB.
    /// Entries larger than this are not cached.
    /// </summary>
    public long MaxEntrySizeBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>
    /// How often to sweep for expired entries in seconds. Default: 5 minutes.
    /// </summary>
    public int EvictionSweepIntervalSeconds { get; init; } = 300;

    /// <summary>
    /// Idle threshold in milliseconds before pre-loading begins. Default: 2 seconds.
    /// </summary>
    public int IdleThresholdMs { get; init; } = 2000;

    /// <summary>
    /// Delay between pre-load requests in milliseconds. Default: 4 seconds.
    /// </summary>
    public int PreloadDelayMs { get; init; } = 4000;

    /// <summary>
    /// Number of nearby links (in each direction) to include as Nearby priority. Default: 3.
    /// </summary>
    public int NearbyLinkRadius { get; init; } = 3;
}
