// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.ValueObjects.Audio;

namespace TermReader.Application.DTOs.Audio;

/// <summary>
/// Request to assemble multiple audio segments into a single M4B file with chapter markers.
/// </summary>
public record AssemblyRequest
{
    /// <summary>
    /// Gets the ordered list of audio segments to assemble.
    /// </summary>
    public required IReadOnlyList<ArticleAudioSegment> Segments { get; init; }

    /// <summary>
    /// Gets the metadata to embed in the output file.
    /// </summary>
    public required AudioMetadata Metadata { get; init; }

    /// <summary>
    /// Gets the output file path for the assembled M4B.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// Gets whether to delete temporary segment files after assembly. Default: true.
    /// </summary>
    public bool CleanupTemporaryFiles { get; init; } = true;
}

/// <summary>
/// A single audio segment representing one article's audio.
/// </summary>
public record ArticleAudioSegment
{
    /// <summary>
    /// Gets the title used as the chapter name.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the path to the audio file for this segment.
    /// </summary>
    public required string AudioFilePath { get; init; }

    /// <summary>
    /// Gets the duration of this segment.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the optional source URL of the article.
    /// </summary>
    public string? SourceUrl { get; init; }
}
