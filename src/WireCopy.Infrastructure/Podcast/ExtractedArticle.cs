// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Represents an article extracted from a reading list item, ready for TTS processing.
/// </summary>
internal sealed record ExtractedArticle
{
    /// <summary>
    /// Gets the article title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the cleaned article text suitable for TTS.
    /// </summary>
    public required string CleanedText { get; init; }

    /// <summary>
    /// Gets the article author, if available.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets the source URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the word count.
    /// </summary>
    public int WordCount { get; init; }

    /// <summary>
    /// Gets the publication date, if available.
    /// </summary>
    public DateTime? PublishedDate { get; init; }
}
