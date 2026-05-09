// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Inspects a loaded page and asks an LLM to discover the headline / byline /
/// publish-date / body / excludes selectors that would extract its article
/// content. The returned <see cref="ArticleSelectorConfig"/> contains a single
/// <see cref="PageTypeEntry"/> for the analyzed page (callers are responsible
/// for merging it with any pre-existing per-domain config).
/// </summary>
public interface IAiArticleExtractor
{
    /// <summary>
    /// Whether the extractor has the credentials it needs to make a live API
    /// call. Pipelines should skip the AI step when this is false rather than
    /// throwing.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Analyzes the page and returns an extraction config. Returns null when
    /// the analyzer is unavailable, the model refuses, or the model's
    /// candidate config fails the self-test gate (the caller must validate
    /// the returned config against the supplied HTML before persisting).
    /// </summary>
    /// <param name="url">Final (post-redirect) page URL.</param>
    /// <param name="html">Raw HTML of the page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Candidate config or null on failure.</returns>
    Task<ArticleSelectorConfig?> AnalyzeAsync(string url, string html, CancellationToken ct);
}
