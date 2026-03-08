// Educational and personal use only.

using TermReader.Domain.ValueObjects.Audio;

namespace TermReader.Application.DTOs.Audio;

/// <summary>
/// Result of an audio assembly operation.
/// </summary>
public record AssemblyResult
{
    /// <summary>
    /// Gets whether the assembly completed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the output file path. Null on failure.
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Gets the optional error message if assembly failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the total duration of the assembled audio.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// Gets the output file size in bytes.
    /// </summary>
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// Gets the chapter markers embedded in the output file.
    /// </summary>
    public IReadOnlyList<AudioChapterMarker> Chapters { get; init; } = [];

    /// <summary>
    /// Creates a successful assembly result.
    /// </summary>
    public static AssemblyResult Successful(
        string outputPath,
        TimeSpan totalDuration,
        long fileSizeBytes,
        IReadOnlyList<AudioChapterMarker> chapters)
    {
        return new AssemblyResult
        {
            Success = true,
            OutputPath = outputPath,
            TotalDuration = totalDuration,
            FileSizeBytes = fileSizeBytes,
            Chapters = chapters,
        };
    }

    /// <summary>
    /// Creates a failed assembly result.
    /// </summary>
    public static AssemblyResult Failure(string errorMessage)
    {
        return new AssemblyResult
        {
            Success = false,
            ErrorMessage = errorMessage,
        };
    }
}
