// Educational and personal use only.

using TermReader.Application.DTOs.Podcast;

namespace TermReader.Application.Interfaces.Podcast;

/// <summary>
/// Cache for TTS-generated audio files. Keyed by content hash so that
/// identical article text reuses previously generated audio.
/// </summary>
public interface ITtsAudioCache
{
    /// <summary>
    /// Tries to retrieve a cached audio entry for the given article text.
    /// </summary>
    /// <param name="articleText">The article text content.</param>
    /// <param name="url">The article URL (used for logging/diagnostics).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache entry if found, null on miss.</returns>
    Task<TtsAudioCacheEntry?> TryGetAsync(string articleText, string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a TTS audio file in the cache.
    /// </summary>
    /// <param name="articleText">The article text content (used to compute cache key).</param>
    /// <param name="url">The article URL.</param>
    /// <param name="title">The article title.</param>
    /// <param name="audioData">The generated audio data to cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache entry that was created.</returns>
    Task<TtsAudioCacheEntry> PutAsync(string articleText, string url, string title, byte[] audioData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes cache coverage for a set of articles.
    /// </summary>
    /// <param name="articles">Articles to analyze, as (url, title, text) tuples.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis with per-article cache status and cost estimates.</returns>
    Task<CacheAnalysis> AnalyzeCollectionAsync(IReadOnlyList<(string Url, string Title, string Text)> articles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets overall cache statistics (entry count, total size, etc.).
    /// </summary>
    Task<TtsCacheStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts expired or least-recently-used entries to reclaim disk space.
    /// </summary>
    Task EvictAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached entries.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
