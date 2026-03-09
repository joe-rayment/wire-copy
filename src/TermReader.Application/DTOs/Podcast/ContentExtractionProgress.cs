// Educational and personal use only.

namespace TermReader.Application.DTOs.Podcast;

/// <summary>
/// Progress update during content extraction from reading list articles.
/// </summary>
public record ContentExtractionProgress
{
    /// <summary>
    /// Gets the 1-based index of the current article.
    /// </summary>
    public required int Current { get; init; }

    /// <summary>
    /// Gets the total number of articles.
    /// </summary>
    public required int Total { get; init; }

    /// <summary>
    /// Gets the title of the current article.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the extraction method currently being used (e.g., "cache", "HTTP", "Selenium", "headed").
    /// </summary>
    public string? ExtractionMethod { get; init; }

    /// <summary>
    /// Gets whether content extraction is complete for this article.
    /// </summary>
    public bool IsCompleted { get; init; }

    /// <summary>
    /// Gets whether content was successfully extracted (only meaningful when <see cref="IsCompleted"/> is true).
    /// </summary>
    public bool IsSuccess { get; init; }
}
