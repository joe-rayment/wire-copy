// Educational and personal use only.

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// Metadata about a web page extracted from HTML.
/// Immutable value object.
/// </summary>
public record PageMetadata
{
    /// <summary>
    /// Page title (from &lt;title&gt; tag or og:title).
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Page description (from meta description or og:description).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Canonical URL of the page (from link rel="canonical" or og:url).
    /// Used to normalize URLs and prevent duplicate history entries.
    /// </summary>
    public string? CanonicalUrl { get; init; }

    /// <summary>
    /// Author of the content (if available).
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Publication date (if available).
    /// </summary>
    public DateTime? PublishedDate { get; init; }

    /// <summary>
    /// Favicon URL (for future enhancement).
    /// </summary>
    public string? FaviconUrl { get; init; }
}
