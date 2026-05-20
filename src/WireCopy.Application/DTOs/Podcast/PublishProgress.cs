// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Per-step progress emitted by <see cref="WireCopy.Application.Interfaces.Podcast.IPodcastPublisher"/>
/// while it uploads episodes and refreshes the feed (workspace-74zy).
/// </summary>
public record PublishProgress
{
    /// <summary>
    /// Total number of episodes the publisher will attempt to upload (after
    /// skip-if-exists is considered).
    /// </summary>
    public required int TotalEpisodes { get; init; }

    /// <summary>
    /// Number of episodes uploaded so far in this publish step. Skipped
    /// already-uploaded episodes are excluded from this count so the
    /// counter tracks real wire activity (workspace-74zy).
    /// </summary>
    public required int UploadedEpisodes { get; init; }

    /// <summary>
    /// Bytes uploaded for the episode currently in flight. Zero when no
    /// byte-level signal is available from the storage client.
    /// </summary>
    public long UploadedBytes { get; init; }

    /// <summary>
    /// Total bytes for the episode currently in flight. Equal to the local
    /// file size when known; zero when the file size could not be probed.
    /// </summary>
    public long UploadedBytesTotal { get; init; }

    /// <summary>
    /// Human-readable status (e.g. "Uploading episode 3 of 5").
    /// </summary>
    public string? Message { get; init; }
}
