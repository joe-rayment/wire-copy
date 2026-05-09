// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Browser;

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

    /// <summary>
    /// Typed human-action signal raised by the preloader when its HTML check
    /// recognises an interstitial (CAPTCHA, login wall, cookie consent, etc.).
    /// Surfaced to the launcher / link-tree status bar so the user is warned
    /// BEFORE they Enter into a doomed article load (workspace-0b9s MVP).
    /// </summary>
    public HumanActionRequired? BlockedAction { get; init; }
}
