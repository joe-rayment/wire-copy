// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

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

    /// <summary>
    /// Wall-clock time elapsed inside the current <see cref="Phase"/> at the
    /// moment this progress event was emitted (workspace-74zy). Lets the
    /// consumer compute a real velocity / ETA without timing the producer
    /// itself.
    /// </summary>
    public TimeSpan PhaseElapsed { get; init; }

    /// <summary>
    /// Current TTS chunk index for the article being voiced (1-based). Only
    /// meaningful during <see cref="PodcastPhase.GeneratingAudio"/> when the
    /// orchestrator is forwarding signals from the TTS service. Zero when
    /// the article hit the audio cache or no chunk has been reported yet
    /// (workspace-74zy).
    /// </summary>
    public int CurrentArticleChunkIndex { get; init; }

    /// <summary>
    /// Total TTS chunks the current article was split into. Reported by
    /// <see cref="WireCopy.Application.DTOs.TtsProgress"/> (workspace-74zy).
    /// </summary>
    public int CurrentArticleChunkTotal { get; init; }

    /// <summary>
    /// Percent (0–100) of the current article's text that the TTS pipeline
    /// has finished synthesising. Computed from TtsProgress.PercentComplete
    /// so weighted-ETA consumers do not have to interpolate (workspace-74zy).
    /// </summary>
    public double CurrentArticleChunkPercent { get; init; }

    /// <summary>
    /// Number of audio segments the M4B assembler has finished concatenating
    /// for the current run (workspace-74zy). Lets the consumer show real
    /// "N of M segments concatenated" copy instead of a frozen percentage
    /// during the assembly phase.
    /// </summary>
    public int AssembledSegments { get; init; }

    /// <summary>
    /// Total audio segments the M4B assembler will concatenate for the
    /// current run (workspace-74zy).
    /// </summary>
    public int AssembledSegmentsTotal { get; init; }

    /// <summary>
    /// Number of episodes the publisher has uploaded so far in the current
    /// publish step (workspace-74zy). Counts only NEW uploads — skipped
    /// already-uploaded episodes don't bump this counter.
    /// </summary>
    public int UploadedEpisodes { get; init; }

    /// <summary>
    /// Total number of episodes the publisher will attempt to upload in the
    /// current publish step (workspace-74zy).
    /// </summary>
    public int UploadedEpisodesTotal { get; init; }

    /// <summary>
    /// Bytes uploaded so far during the in-flight episode upload
    /// (workspace-74zy). Zero when the storage client did not surface
    /// byte-level progress. Lets the consumer show a per-episode upload bar
    /// during the long opaque publish phase.
    /// </summary>
    public long UploadedBytes { get; init; }

    /// <summary>
    /// Total bytes for the in-flight episode upload (workspace-74zy). Equal
    /// to the local file size when known; zero otherwise.
    /// </summary>
    public long UploadedBytesTotal { get; init; }

    /// <summary>
    /// Number of articles served from the TTS audio cache during this run
    /// (workspace-74zy). Reported once at the start of the audio phase so
    /// weighted-ETA consumers can correctly weight upload-bound vs. TTS-bound
    /// runs.
    /// </summary>
    public int CachedArticleCount { get; init; }

    /// <summary>
    /// Number of articles that needed fresh TTS generation in this run
    /// (workspace-74zy).
    /// </summary>
    public int UncachedArticleCount { get; init; }
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
