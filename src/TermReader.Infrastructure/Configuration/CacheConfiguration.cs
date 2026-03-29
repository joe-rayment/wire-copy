// Educational and personal use only.

namespace TermReader.Infrastructure.Configuration;

public class CacheConfiguration
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Maximum cache size in bytes. Default: 100 MB.
    /// </summary>
    public long MaxSizeBytes { get; init; } = 200 * 1024 * 1024;

    /// <summary>
    /// Maximum number of cache entries. Default: 200.
    /// </summary>
    public int MaxEntries { get; init; } = 200;

    /// <summary>
    /// Default time-to-live for cache entries in seconds. Default: 24 hours.
    /// </summary>
    public int DefaultTtlSeconds { get; init; } = 86400;

    /// <summary>
    /// Time-to-live for link-list/section page cache entries in seconds. Default: 1 hour.
    /// Shorter than article TTL because section pages change frequently (new headlines).
    /// </summary>
    public int LinkListTtlSeconds { get; init; } = 3600;

    /// <summary>
    /// Maximum size of a single cache entry in bytes. Default: 15 MB.
    /// JS-rendered pages (NYT, etc.) can be 5-10 MB after browser rendering.
    /// </summary>
    public long MaxEntrySizeBytes { get; init; } = 15 * 1024 * 1024;

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
    /// Delay between pre-load requests to paywalled domains in milliseconds. Default: 6000ms.
    /// Longer than normal to avoid bot detection on authenticated sessions.
    /// </summary>
    public int PaywalledDomainDelayMs { get; init; } = 6000;

    /// <summary>
    /// Maximum number of articles to pre-load per session for paywalled domains. Default: 15.
    /// Limits bulk fetching to avoid appearing as a scraper.
    /// </summary>
    public int MaxPaywalledPreloads { get; init; } = 15;

    /// <summary>
    /// Maximum number of links to pre-load per page/collection. Default: 50.
    /// After priority sorting, only the top N items are kept in the queue.
    /// </summary>
    public int MaxPreloadLinks { get; init; } = 50;

    /// <summary>
    /// Whether to persist cache entries to disk across app restarts. Default: true.
    /// </summary>
    public bool DiskCacheEnabled { get; init; } = true;

    /// <summary>
    /// Directory for disk cache files. Default: {LocalAppData}/TermReader/page-cache/.
    /// When null, the default path is used.
    /// </summary>
    public string? DiskCachePath { get; init; }

    /// <summary>
    /// Maximum total size of all disk cache files in bytes. Default: 500 MB.
    /// When the limit is exceeded, the oldest files (by last write time) are evicted first.
    /// Set to 0 to disable disk size enforcement.
    /// </summary>
    public long MaxDiskSizeBytes { get; init; } = 500L * 1024 * 1024;
}
