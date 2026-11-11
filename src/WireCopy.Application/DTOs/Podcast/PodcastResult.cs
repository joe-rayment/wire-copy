// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Application.DTOs.Podcast;

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
    /// Gets the title of the audio-engine feed the episode was published to; null when not published.
    /// </summary>
    public string? FeedTitle { get; init; }

    /// <summary>
    /// Gets the episode's audio URL in audio-engine's feed; null when not published.
    /// </summary>
    public string? EpisodeUrl { get; init; }

    /// <summary>
    /// Gets a one-line publishing note for the result screen (not published, still publishing); null when published normally.
    /// </summary>
    public string? PublicationNote { get; init; }

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
    /// Gets the number of articles served from the TTS cache.
    /// </summary>
    public int ArticlesCached { get; init; }

    /// <summary>
    /// Gets the estimated TTS API cost in USD.
    /// </summary>
    public decimal TotalCost { get; init; }

    /// <summary>
    /// Gets an optional error message if the pipeline failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets per-article failure details (content extraction or TTS failures).
    /// </summary>
    public IReadOnlyList<ArticleFailure> FailedArticleDetails { get; init; } = [];

    /// <summary>
    /// Gets the explicit classification of the result (workspace-3a2k).
    /// Defaults to <c>null</c> meaning "let the screen infer from
    /// <see cref="Success"/> + <see cref="FeedUrl"/> + <see cref="ArticlesFailed"/>".
    /// The orchestrator sets this explicitly for the publish-failed-in-audio-engine
    /// path, which must render as <see cref="PodcastResultClassification.TotalFailure"/>
    /// rather than the legacy "local-only success" silent-degradation behaviour.
    /// </summary>
    public PodcastResultClassification? Classification { get; init; }

    /// <summary>
    /// Gets the typed failure detail (workspace-3a2k). Non-null when
    /// <see cref="Classification"/> is <see cref="PodcastResultClassification.TotalFailure"/>;
    /// drives the result screen's typed (Step, Reason, Fix) tuple.
    /// </summary>
    public PodcastFailureDetail? FailureDetail { get; init; }

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
        int articlesCached = 0,
        decimal totalCost = 0m,
        IReadOnlyList<ArticleFailure>? failedArticleDetails = null,
        string? feedTitle = null,
        string? episodeUrl = null,
        string? publicationNote = null) => new()
    {
        Success = true,
        FeedUrl = feedUrl,
        FeedTitle = feedTitle,
        EpisodeUrl = episodeUrl,
        PublicationNote = publicationNote,
        LocalFilePath = localFilePath,
        TotalDuration = totalDuration,
        ArticlesProcessed = articlesProcessed,
        ArticlesFailed = articlesFailed,
        FileSizeBytes = fileSizeBytes,
        ArticlesCached = articlesCached,
        TotalCost = totalCost,
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
        Classification = PodcastResultClassification.TotalFailure,
    };

    /// <summary>
    /// Creates a typed failure result (workspace-3a2k Phase E). Used by the
    /// orchestrator when the publish step fails in audio-engine — the
    /// detail drives targeted remediation copy on the result screen instead of the heuristic string-pattern fallback.
    /// </summary>
    public static PodcastResult Failure(
        string errorMessage,
        PodcastFailureDetail detail,
        string? localFilePath = null,
        IReadOnlyList<ArticleFailure>? failedArticleDetails = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        LocalFilePath = localFilePath,
        FailedArticleDetails = failedArticleDetails ?? [],
        Classification = PodcastResultClassification.TotalFailure,
        FailureDetail = detail,
    };
}
