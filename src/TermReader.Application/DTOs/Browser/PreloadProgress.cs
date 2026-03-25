// Educational and personal use only.

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Progress information for background page pre-loading.
/// </summary>
public record PreloadProgress
{
    /// <summary>
    /// Total same-origin content links eligible for HTTP pre-loading
    /// (excludes needs-JS domains).
    /// </summary>
    public int TotalCacheableLinks { get; init; }

    /// <summary>
    /// Number of eligible links currently in the cache.
    /// </summary>
    public int CachedCount { get; init; }

    /// <summary>
    /// Number of links on domains that require JS rendering
    /// (cannot be pre-loaded via HTTP).
    /// </summary>
    public int NeedsBrowserCount { get; init; }

    /// <summary>
    /// Whether all cacheable links have been processed
    /// (cached or identified as needing browser).
    /// </summary>
    public bool IsComplete => CachedCount + NeedsBrowserCount >= TotalCacheableLinks;

    /// <summary>
    /// Number of same-origin content links on paywalled domains
    /// that cannot be HTTP pre-loaded (need browser with cookies).
    /// </summary>
    public int PaywalledLinkCount { get; init; }

    /// <summary>
    /// Whether the preloader is actively fetching pages right now.
    /// </summary>
    public bool IsActivelyFetching { get; init; }

    /// <summary>
    /// The URL currently being fetched, if any. Shown in the status bar
    /// so the user knows exactly what article is being cached right now.
    /// </summary>
    public string? CurrentlyFetchingUrl { get; init; }
}
