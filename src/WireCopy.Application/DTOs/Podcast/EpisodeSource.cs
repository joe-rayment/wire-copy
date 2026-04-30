// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Source data for creating a podcast episode from a locally generated audio file.
/// </summary>
public record EpisodeSource
{
    /// <summary>
    /// Gets the episode title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the episode description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the path to the local audio file.
    /// </summary>
    public required string LocalAudioFilePath { get; init; }

    /// <summary>
    /// Gets the duration of the audio file.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the optional URL to the original source article.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Gets the optional chapter markers for the episode.
    /// </summary>
    public IReadOnlyList<ChapterMark>? Chapters { get; init; }
}
