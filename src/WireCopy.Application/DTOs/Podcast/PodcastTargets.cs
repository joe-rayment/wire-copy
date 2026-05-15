// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Destination paths for a podcast generation run — resolved BEFORE generation
/// starts so the progress screen footer can show the user where the result is
/// going to land (workspace-zh3u).
/// </summary>
public record PodcastTargets
{
    /// <summary>
    /// Absolute path on disk where the assembled M4B will be written.
    /// Always present.
    /// </summary>
    public required string LocalFilePath { get; init; }

    /// <summary>
    /// Public URL the feed will be published at, or null if no GCS bucket
    /// is configured (local-only mode).
    /// </summary>
    public string? FeedUrl { get; init; }
}
