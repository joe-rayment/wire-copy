// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.DTOs;

/// <summary>
/// Progress update during TTS generation, reported per chunk.
/// </summary>
public record TtsProgress
{
    /// <summary>
    /// Gets the current chunk being processed (1-based).
    /// </summary>
    public required int CurrentChunk { get; init; }

    /// <summary>
    /// Gets the total number of chunks to process.
    /// </summary>
    public required int TotalChunks { get; init; }

    /// <summary>
    /// Gets the number of characters processed so far.
    /// </summary>
    public required int CharactersProcessed { get; init; }

    /// <summary>
    /// Gets the total number of characters to process.
    /// </summary>
    public required int TotalCharacters { get; init; }

    /// <summary>
    /// Gets the completion percentage (0-100).
    /// </summary>
    public double PercentComplete => TotalCharacters > 0
        ? Math.Round((double)CharactersProcessed / TotalCharacters * 100, 1)
        : 0;

    /// <summary>
    /// Gets a human-readable progress message.
    /// </summary>
    public required string Message { get; init; }
}
