// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Audio;

/// <summary>
/// Represents a chapter marker within an audio file, used for M4B chapter navigation.
/// </summary>
public record AudioChapterMarker
{
    /// <summary>
    /// Gets the chapter title displayed in audio players.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the start time of the chapter within the audio file.
    /// </summary>
    public required TimeSpan StartTime { get; init; }

    /// <summary>
    /// Gets the optional end time of the chapter. If null, the chapter extends to the next chapter or end of file.
    /// </summary>
    public TimeSpan? EndTime { get; init; }

    /// <summary>
    /// Gets the optional author of the chapter content.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets the optional source URL where the chapter content was originally published.
    /// </summary>
    public string? SourceUrl { get; init; }
}
