// Educational and personal use only.

using TermReader.Application.DTOs.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Cache for storing loaded page results to avoid redundant network fetches.
/// </summary>
public interface IPageCache
{
    /// <summary>
    /// Tries to get a cached page result by URL.
    /// Returns null if the URL is not cached or the entry has expired.
    /// </summary>
    /// <param name="url">URL to look up (will be normalized internally).</param>
    /// <returns>Cached page load result, or null if not found/expired.</returns>
    PageLoadResult? TryGet(string url);

    /// <summary>
    /// Stores a page load result in the cache.
    /// Evicts entries if necessary to stay within size/count limits.
    /// </summary>
    /// <param name="requestUrl">Original request URL.</param>
    /// <param name="result">Page load result to cache.</param>
    void Put(string requestUrl, PageLoadResult result);

    /// <summary>
    /// Removes a specific URL from the cache.
    /// </summary>
    /// <param name="url">URL to remove (will be normalized internally).</param>
    /// <returns>True if the entry was found and removed.</returns>
    bool Remove(string url);

    /// <summary>
    /// Checks whether a URL is currently cached (and not expired).
    /// </summary>
    /// <param name="url">URL to check (will be normalized internally).</param>
    bool Contains(string url);

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    /// <returns>Stats about what was cleared.</returns>
    CacheStats Clear();

    /// <summary>
    /// Gets current cache statistics.
    /// </summary>
    CacheStats GetStats();

    /// <summary>
    /// Gets the set of currently cached request URLs.
    /// Used by renderers to show cache indicators.
    /// </summary>
    IReadOnlySet<string> GetCachedUrls();

    /// <summary>
    /// Gets the UTC timestamp when a URL was cached.
    /// Returns null if not cached.
    /// </summary>
    /// <param name="url">URL to look up (will be normalized internally).</param>
    DateTime? GetCachedAt(string url);

    /// <summary>
    /// Gets the cached build result (extracted links, hierarchy, readable content)
    /// for a URL. Returns null if not cached or expired.
    /// Build cache is stored alongside the page load result and evicted together.
    /// </summary>
    PageBuildCache? TryGetBuildCache(string url);

    /// <summary>
    /// Stores a build cache result alongside an existing page load result.
    /// No-op if the URL is not in the page cache.
    /// </summary>
    void PutBuildCache(string url, PageBuildCache buildCache);

    /// <summary>
    /// Updates the TTL of an existing cache entry.
    /// No-op if the URL is not cached.
    /// </summary>
    void UpdateTtl(string url, int ttlSeconds);

    /// <summary>
    /// Applies the shorter link-list TTL to a cached entry.
    /// Uses the configured LinkListTtlSeconds value. No-op if not cached.
    /// </summary>
    void ApplyLinkListTtl(string url);
}
