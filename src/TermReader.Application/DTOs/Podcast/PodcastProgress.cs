// Educational and personal use only.

namespace TermReader.Application.DTOs.Podcast;

/// <summary>
/// Progress update during podcast generation.
/// </summary>
public record PodcastProgress
{
    /// <summary>
    /// Gets the current phase of the pipeline.
    /// </summary>
    public required PodcastPhase Phase { get; init; }

    /// <summary>
    /// Gets the 1-based index of the current article being processed.
    /// </summary>
    public int CurrentArticle { get; init; }

    /// <summary>
    /// Gets the total number of articles.
    /// </summary>
    public int TotalArticles { get; init; }

    /// <summary>
    /// Gets the title of the article currently being processed.
    /// </summary>
    public string? ArticleTitle { get; init; }

    /// <summary>
    /// Gets the overall percent complete (0-100).
    /// </summary>
    public int PercentComplete { get; init; }

    /// <summary>
    /// Gets an optional status message for the current step.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Whether the current article was served from the TTS audio cache.
    /// </summary>
    public bool IsFromCache { get; init; }

    /// <summary>
    /// Gets the content extraction method being used (e.g., "cache", "HTTP", "browser", "headed").
    /// Only set during <see cref="PodcastPhase.CachingContent"/>.
    /// </summary>
    public string? ExtractionMethod { get; init; }

    /// <summary>
    /// Gets whether content extraction is complete for the current article.
    /// Only set during <see cref="PodcastPhase.CachingContent"/>.
    /// </summary>
    public bool IsArticleComplete { get; init; }

    /// <summary>
    /// Gets whether content extraction succeeded for the current article.
    /// Only meaningful when <see cref="IsArticleComplete"/> is true.
    /// </summary>
    public bool IsArticleSuccess { get; init; }
}

/// <summary>
/// Phases of the podcast generation pipeline.
/// </summary>
public enum PodcastPhase
{
    /// <summary>
    /// Loading and extracting article content from the reading list.
    /// </summary>
    CachingContent,

    /// <summary>
    /// Generating audio via TTS for each article.
    /// </summary>
    GeneratingAudio,

    /// <summary>
    /// Assembling individual audio files into a single M4B.
    /// </summary>
    AssemblingAudio,

    /// <summary>
    /// Uploading the M4B and publishing the RSS feed.
    /// </summary>
    Publishing,
}
