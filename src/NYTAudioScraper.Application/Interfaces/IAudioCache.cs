namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Specialized cache for audio files with disk-based storage and content hashing
/// </summary>
public interface IAudioCache
{
    /// <summary>
    /// Gets cached audio by content hash
    /// </summary>
    /// <param name="content">Text content to hash</param>
    /// <param name="voiceId">Voice ID used for generation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached audio data or null if not found</returns>
    Task<byte[]?> GetAsync(string content, string voiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Caches audio data with content hash as key
    /// </summary>
    /// <param name="content">Text content to hash</param>
    /// <param name="voiceId">Voice ID used for generation</param>
    /// <param name="audioData">Audio data to cache</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetAsync(string content, string voiceId, byte[] audioData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes expired cache entries
    /// </summary>
    /// <param name="maxAgedays">Maximum age in days (default: 30)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CleanupAsync(int maxAgeDays = 30, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total size of cached audio files in bytes
    /// </summary>
    Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default);
}
