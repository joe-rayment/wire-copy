// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.DTOs.Podcast;

/// <summary>
/// Cache status for a single article in a collection.
/// </summary>
public record ArticleCacheStatus
{
    /// <summary>
    /// URL of the article.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Display title of the article.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Whether TTS audio for this article is already cached.
    /// </summary>
    public required bool IsCached { get; init; }

    /// <summary>
    /// Estimated TTS generation cost in USD for this article (0 if cached).
    /// </summary>
    public required decimal EstimatedCost { get; init; }
}
