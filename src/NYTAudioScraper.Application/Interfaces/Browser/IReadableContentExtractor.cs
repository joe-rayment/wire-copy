// Educational and personal use only.

using NYTAudioScraper.Domain.Entities.Browser;

namespace NYTAudioScraper.Application.Interfaces.Browser;

/// <summary>
/// Service for extracting clean, readable article content from HTML.
/// Wraps the existing ArticleParser for browser mode.
/// </summary>
public interface IReadableContentExtractor
{
    /// <summary>
    /// Extracts readable content from HTML.
    /// Returns null if the page is not an article.
    /// </summary>
    /// <param name="html">Raw HTML content.</param>
    /// <param name="url">URL of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Readable content or null if not an article.</returns>
    Task<ReadableContent?> ExtractAsync(string html, string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines if the HTML content appears to be an article.
    /// </summary>
    /// <param name="html">Raw HTML content.</param>
    /// <returns>True if the page appears to be an article.</returns>
    bool IsArticle(string html);
}
