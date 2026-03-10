// Educational and personal use only.

using TermReader.Application.DTOs.Podcast;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Application.Interfaces.Podcast;

/// <summary>
/// Orchestrates the end-to-end podcast generation pipeline:
/// content extraction, TTS, M4B assembly, and feed publishing.
/// </summary>
public interface IPodcastOrchestrator
{
    /// <summary>
    /// Generates a podcast from a reading list collection.
    /// </summary>
    /// <param name="collection">The reading list collection containing articles to convert.</param>
    /// <param name="progress">Optional progress callback for UI updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the podcast generation pipeline.</returns>
    Task<PodcastResult> GeneratePodcastAsync(
        Collection collection,
        IProgress<PodcastProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes cache coverage for articles in a collection.
    /// Used for pre-flight cost estimation on the confirmation screen.
    /// </summary>
    /// <param name="collection">The reading list collection to analyze.</param>
    /// <param name="progress">Optional progress callback for UI updates during content extraction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cache analysis with per-article status and cost estimates.</returns>
    Task<CacheAnalysis> AnalyzeCacheStatusAsync(
        Collection collection,
        IProgress<ContentExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
