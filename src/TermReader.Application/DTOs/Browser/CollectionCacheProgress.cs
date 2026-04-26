// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Cache state for a single article in a collection.
/// </summary>
public enum ArticleCacheState
{
    /// <summary>
    /// Article content is already in the page cache.
    /// </summary>
    Cached,

    /// <summary>
    /// Article is currently being fetched by the preload service.
    /// </summary>
    Caching,

    /// <summary>
    /// Article requires a full browser (JS rendering) and cannot be HTTP-preloaded.
    /// </summary>
    NeedsBrowser,

    /// <summary>
    /// Article has not been cached or attempted yet.
    /// </summary>
    Pending
}

/// <summary>
/// Cache status for a single article within a collection.
/// </summary>
public record ArticleCacheStatus
{
    /// <summary>
    /// URL of the article.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Display title of the article.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Current cache state of the article.
    /// </summary>
    public required ArticleCacheState State { get; init; }
}

/// <summary>
/// Per-article cache progress for a collection, used to drive the cache-wait screen.
/// </summary>
public record CollectionCacheProgress
{
    /// <summary>
    /// Per-article cache statuses in collection order.
    /// </summary>
    public required IReadOnlyList<ArticleCacheStatus> Articles { get; init; }

    /// <summary>
    /// Total number of articles in the collection.
    /// </summary>
    public int Total => Articles.Count;

    /// <summary>
    /// Number of articles already cached.
    /// </summary>
    public int CachedCount => Articles.Count(a => a.State == ArticleCacheState.Cached);

    /// <summary>
    /// Number of articles that require browser rendering.
    /// </summary>
    public int NeedsBrowserCount => Articles.Count(a => a.State == ArticleCacheState.NeedsBrowser);

    /// <summary>
    /// Number of articles still pending (not cached, not in-flight, not needs-browser).
    /// </summary>
    public int PendingCount => Articles.Count(a => a.State == ArticleCacheState.Pending);

    /// <summary>
    /// Whether all articles are either cached or identified as needing browser.
    /// </summary>
    public bool IsComplete => Articles.All(a =>
        a.State is ArticleCacheState.Cached or ArticleCacheState.NeedsBrowser);
}
