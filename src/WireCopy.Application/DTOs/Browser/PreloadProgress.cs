// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;

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

    /// <summary>
    /// Current per-URL stage the preloader is in (workspace-7xw0). Drives
    /// the prefetch detail panel'\''s "now: loading / extracting / caching"
    /// label so the user can tell whether the loader is making progress.
    /// </summary>
    public PreloadStage CurrentStage { get; init; } = PreloadStage.Idle;

    /// <summary>
    /// True while prefetch is paused because the USER is using the shared browser
    /// (workspace-mya7); it resumes automatically once the browser goes quiet.
    /// </summary>
    public bool PausedByUser { get; init; }

    /// <summary>
    /// Snapshot of the next URLs queued for prefetch (workspace-7xw0). Used
    /// by the prefetch detail panel'\''s "up next" list. Empty when the queue
    /// is drained or has been throttled by an idle-detect / circuit-break.
    /// </summary>
    public IReadOnlyList<string> UpcomingUrls { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Most-recent prefetch outcomes (workspace-7xw0). Bounded ring buffer
    /// of up to ~20 entries. The detail panel uses this to spot stalls and
    /// repeated skips/failures on the same origin.
    /// </summary>
    public IReadOnlyList<PreloadHistoryEntry> RecentItems { get; init; } = System.Array.Empty<PreloadHistoryEntry>();

    /// <summary>
    /// Wall-clock time elapsed on <see cref="CurrentlyFetchingUrl"/> since it
    /// became the in-flight item (workspace-fh7g). Null when no fetch is in
    /// flight. The detail panel renders this in warning style past 8s and adds
    /// a "looks stuck — Shift+R to retry" hint past 30s so the user can tell
    /// whether prefetch is progressing or jammed on one URL.
    /// </summary>
    public TimeSpan? ElapsedOnCurrent { get; init; }
}
