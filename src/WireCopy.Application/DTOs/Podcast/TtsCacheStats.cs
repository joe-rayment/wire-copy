// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Statistics about the TTS audio cache.
/// </summary>
public record TtsCacheStats
{
    /// <summary>
    /// Number of cached audio entries.
    /// </summary>
    public required int EntryCount { get; init; }

    /// <summary>
    /// Total size of all cached audio files in bytes.
    /// </summary>
    public required long TotalSizeBytes { get; init; }

    /// <summary>
    /// Total size formatted as a human-readable string.
    /// </summary>
    public string FormattedSize => TotalSizeBytes switch
    {
        < 1024 => $"{TotalSizeBytes} B",
        < 1024 * 1024 => $"{TotalSizeBytes / 1024.0:F1} KB",
        _ => $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB"
    };
}
