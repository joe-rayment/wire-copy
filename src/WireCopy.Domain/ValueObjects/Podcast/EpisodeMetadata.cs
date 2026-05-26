// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Podcast;

/// <summary>
/// Metadata for a single podcast episode in the RSS feed.
/// </summary>
public record EpisodeMetadata
{
    /// <summary>
    /// Gets the unique identifier for this episode.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the episode title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the episode description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the UTC publication timestamp.
    /// </summary>
    public required DateTime PublishedAtUtc { get; init; }

    /// <summary>
    /// Gets the public URL to the audio file.
    /// </summary>
    public required string AudioUrl { get; init; }

    /// <summary>
    /// Gets the audio file size in bytes.
    /// </summary>
    public required long AudioSizeBytes { get; init; }

    /// <summary>
    /// Gets the episode duration.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the audio MIME type (e.g., "audio/x-m4a", "audio/mpeg").
    /// </summary>
    public required string AudioMimeType { get; init; }

    /// <summary>
    /// Gets the optional list of chapter markers for this episode.
    /// </summary>
    public IReadOnlyList<ChapterMark>? Chapters { get; init; }

    /// <summary>
    /// Optional public URL of the Podcasting 2.0 chapters JSON sidecar
    /// for this episode (workspace-2g70). When set, the feed serializes a
    /// <c>&lt;podcast:chapters url="…" type="application/json+chapters"/&gt;</c>
    /// element that Apple Podcasts / Pocket Casts / Spotify all read.
    /// </summary>
    public string? ChaptersJsonUrl { get; init; }

    /// <summary>
    /// Gets the optional URL to the original source article.
    /// </summary>
    public string? SourceUrl { get; init; }
}
