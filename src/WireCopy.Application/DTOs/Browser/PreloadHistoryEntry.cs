// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// One entry in the prefetch history ring buffer (workspace-7xw0). The
/// detail panel renders the most recent N entries with their outcome glyph
/// (<c>✓</c> cached, <c>✗</c> failed, <c>⏭</c> skipped) so the user can
/// scan recent work and spot stalls.
/// </summary>
public sealed record PreloadHistoryEntry
{
    /// <summary>URL the loader was working on (raw, not normalised).</summary>
    public required string Url { get; init; }

    /// <summary>How the load attempt concluded.</summary>
    public required PreloadOutcome Outcome { get; init; }

    /// <summary>Wall-clock time from dequeue to completion, in milliseconds.</summary>
    public required long ElapsedMs { get; init; }

    /// <summary>
    /// Short human-readable reason. Populated when the entry is a skip or
    /// failure; null for plain success. Surfaced in the detail panel so the
    /// user can see why a URL was skipped (paywall, needs JS, 403, …).
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Categorical outcome of one prefetch attempt (workspace-7xw0). Drives the
/// glyph rendered in the detail panel and the colour treatment (success
/// stays muted; failure / skip gets a warning tone).
/// </summary>
public enum PreloadOutcome
{
    /// <summary>Page was fetched, extracted, and cached successfully.</summary>
    Cached,

    /// <summary>Page was deliberately skipped (paywall preview, needs-JS domain, circuit-broken, ...).</summary>
    Skipped,

    /// <summary>Page fetch or processing failed.</summary>
    Failed,
}
