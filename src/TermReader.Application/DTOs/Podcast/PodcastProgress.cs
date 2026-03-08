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
