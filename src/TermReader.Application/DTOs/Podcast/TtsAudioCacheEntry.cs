// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.DTOs.Podcast;

/// <summary>
/// Represents a cached TTS audio file entry.
/// </summary>
public record TtsAudioCacheEntry
{
    /// <summary>
    /// Cache key derived from content hash and TTS configuration.
    /// </summary>
    public required string CacheKey { get; init; }

    /// <summary>
    /// Path to the cached audio file on disk.
    /// </summary>
    public required string AudioFilePath { get; init; }

    /// <summary>
    /// Size of the cached audio file in bytes.
    /// </summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>
    /// When this entry was cached.
    /// </summary>
    public required DateTime CachedAtUtc { get; init; }

    /// <summary>
    /// Hash of the article text content.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Hash of the TTS configuration (voice, model, etc.) used to generate this audio.
    /// </summary>
    public required string TtsConfigHash { get; init; }
}
