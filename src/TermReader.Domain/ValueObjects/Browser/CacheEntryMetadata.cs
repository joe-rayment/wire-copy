// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// Metadata for a cached page entry.
/// </summary>
public record CacheEntryMetadata
{
    /// <summary>
    /// Original request URL (before redirects).
    /// </summary>
    public required string RequestUrl { get; init; }

    /// <summary>
    /// Final URL after redirects.
    /// </summary>
    public required string FinalUrl { get; init; }

    /// <summary>
    /// Normalized cache key URL.
    /// </summary>
    public required string NormalizedUrl { get; init; }

    /// <summary>
    /// When the page was originally fetched.
    /// </summary>
    public DateTime CachedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this cache entry expires.
    /// </summary>
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>
    /// When this entry was last accessed (for LRU eviction).
    /// </summary>
    public DateTime LastAccessedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Estimated size of the cached content in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Whether this entry has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
}
