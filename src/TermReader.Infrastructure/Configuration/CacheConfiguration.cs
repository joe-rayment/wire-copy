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
    /// Delay between pre-load requests in milliseconds. Default: 2 seconds.
    /// </summary>
    public int PreloadDelayMs { get; init; } = 2000;

    /// <summary>
    /// Cooldown in seconds before retrying a circuit-broken domain. Default: 5 minutes.
    /// </summary>
    public int CircuitBreakerCooldownSeconds { get; init; } = 300;

    /// <summary>
    /// When enabled, uses shorter delay for cross-domain requests (different domain than the last
    /// request) while keeping full delay for same-domain requests. Default: true.
    /// </summary>
    public bool AdaptiveRateLimitEnabled { get; init; } = true;

    /// <summary>
    /// Delay between pre-load requests to different domains in milliseconds. Default: 1500ms.
    /// Only used when <see cref="AdaptiveRateLimitEnabled"/> is true.
    /// </summary>
    public int CrossDomainDelayMs { get; init; } = 1500;

    /// <summary>
    /// Maximum number of links to pre-load per page/collection. Default: 20.
    /// After priority sorting, only the top N items are kept in the queue.
    /// </summary>
    public int MaxPreloadLinks { get; init; } = 20;

    /// <summary>
    /// Whether to persist cache entries to disk across app restarts. Default: true.
    /// </summary>
    public bool DiskCacheEnabled { get; init; } = true;

    /// <summary>
    /// Directory for disk cache files. Default: {LocalAppData}/TermReader/page-cache/.
    /// When null, the default path is used.
    /// </summary>
    public string? DiskCachePath { get; init; }
}
