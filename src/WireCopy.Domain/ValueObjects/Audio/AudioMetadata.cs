// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Audio;

/// <summary>
/// Metadata embedded in an audio file (M4B/M4A tags).
/// </summary>
public record AudioMetadata
{
    /// <summary>
    /// Gets the title of the audio file.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the optional author or artist name.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets the optional description or comment.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the optional publication date of the source content.
    /// </summary>
    public DateTime? PublishedDate { get; init; }

    /// <summary>
    /// Gets the optional path to cover art image file.
    /// </summary>
    public string? CoverArtPath { get; init; }

    /// <summary>
    /// Gets the optional genre tag.
    /// </summary>
    public string? Genre { get; init; }
}
