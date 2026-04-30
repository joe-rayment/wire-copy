// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Podcast;

/// <summary>
/// A chapter marker within a podcast episode, used for enhanced podcast players.
/// </summary>
public record ChapterMark
{
    /// <summary>
    /// Gets the chapter title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the start time of the chapter within the episode.
    /// </summary>
    public required TimeSpan StartTime { get; init; }

    /// <summary>
    /// Gets the optional URL to chapter artwork.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Gets the optional URL linked from this chapter.
    /// </summary>
    public string? LinkUrl { get; init; }
}
