// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Applies a saved <see cref="ArticleSelectorConfig"/> to a loaded HTML page,
/// picks the highest-priority matching <see cref="PageTypeEntry"/>, and runs
/// its selectors to produce a <see cref="ReadableContent"/>.
/// </summary>
public interface ISelectorBasedArticleExtractor
{
    /// <summary>
    /// Extracts article content using the supplied per-domain config.
    /// Returns null when no <see cref="PageTypeEntry"/> matches, the matched
    /// entry's selectors produce no body content, or the result fails the
    /// entry's quality thresholds.
    /// </summary>
    /// <param name="config">Saved per-domain layout.</param>
    /// <param name="url">Final (post-redirect) page URL.</param>
    /// <param name="html">Raw HTML for the page.</param>
    /// <returns>Extracted content or null on miss.</returns>
    ReadableContent? Extract(ArticleSelectorConfig config, string url, string html);

    /// <summary>
    /// Picks the highest-priority <see cref="PageTypeEntry"/> whose matcher
    /// accepts the page, or null when no entry matches. Exposed so callers
    /// (and tests) can introspect the matcher's resolution without paying for
    /// the body extraction.
    /// </summary>
    PageTypeEntry? PickEntry(ArticleSelectorConfig config, string url, string html);
}
