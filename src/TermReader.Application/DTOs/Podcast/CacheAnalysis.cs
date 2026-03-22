// Educational and personal use only.

namespace TermReader.Application.DTOs.Podcast;

/// <summary>
/// Analysis of TTS audio cache coverage for a collection.
/// </summary>
public record CacheAnalysis
{
    /// <summary>
    /// Total number of articles in the collection.
    /// </summary>
    public required int TotalArticles { get; init; }

    /// <summary>
    /// Number of articles with cached TTS audio.
    /// </summary>
    public required int CachedArticles { get; init; }

    /// <summary>
    /// Number of articles that need TTS generation.
    /// </summary>
    public required int UncachedArticles { get; init; }

    /// <summary>
    /// Estimated total cost in USD for generating uncached articles.
    /// </summary>
    public required decimal EstimatedCost { get; init; }

    /// <summary>
    /// Per-article cache status details.
    /// </summary>
    public required IReadOnlyList<ArticleCacheStatus> ArticleStatuses { get; init; }
}
