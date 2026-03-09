// Educational and personal use only.

namespace TermReader.Application.DTOs.Podcast;

/// <summary>
/// Result of the end-to-end podcast generation pipeline.
/// </summary>
public record PodcastResult
{
    /// <summary>
    /// Gets whether the pipeline completed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the published RSS feed URL, if publishing succeeded.
    /// </summary>
    public string? FeedUrl { get; init; }

    /// <summary>
    /// Gets the local M4B file path, if assembly succeeded.
    /// </summary>
    public string? LocalFilePath { get; init; }

    /// <summary>
    /// Gets the total duration of the generated audio.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// Gets the number of articles successfully processed.
    /// </summary>
    public int ArticlesProcessed { get; init; }

    /// <summary>
    /// Gets the number of articles that failed during processing.
    /// </summary>
    public int ArticlesFailed { get; init; }

    /// <summary>
    /// Gets the M4B file size in bytes.
    /// </summary>
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// Gets an optional error message if the pipeline failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets per-article failure details (content extraction or TTS failures).
    /// </summary>
    public IReadOnlyList<ArticleFailure> FailedArticleDetails { get; init; } = [];

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static PodcastResult Successful(
        string? feedUrl,
        string localFilePath,
        TimeSpan totalDuration,
        int articlesProcessed,
        int articlesFailed,
        long fileSizeBytes,
        IReadOnlyList<ArticleFailure>? failedArticleDetails = null) => new()
    {
        Success = true,
        FeedUrl = feedUrl,
        LocalFilePath = localFilePath,
        TotalDuration = totalDuration,
        ArticlesProcessed = articlesProcessed,
        ArticlesFailed = articlesFailed,
        FileSizeBytes = fileSizeBytes,
        FailedArticleDetails = failedArticleDetails ?? [],
    };

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static PodcastResult Failure(
        string errorMessage,
        string? localFilePath = null,
        IReadOnlyList<ArticleFailure>? failedArticleDetails = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        LocalFilePath = localFilePath,
        FailedArticleDetails = failedArticleDetails ?? [],
    };
}
