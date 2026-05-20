// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs;

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

    /// <summary>
    /// workspace-rz1c: whether this report is announcing a transient-error
    /// retry rather than forward progress. When true, the consumer should
    /// render a "rate-limited / retrying" banner instead of advancing the
    /// progress bar.
    /// </summary>
    public bool IsRetrying { get; init; }

    /// <summary>
    /// workspace-rz1c: 1-based retry attempt number (e.g. 1 = first retry).
    /// Only meaningful when <see cref="IsRetrying"/> is true.
    /// </summary>
    public int RetryAttempt { get; init; }

    /// <summary>
    /// workspace-rz1c: configured maximum number of retries. Only meaningful
    /// when <see cref="IsRetrying"/> is true.
    /// </summary>
    public int RetryMaxAttempts { get; init; }

    /// <summary>
    /// workspace-rz1c: seconds we will sleep before the next attempt. Lets
    /// the consumer render an explicit "retrying in Xs" countdown rather
    /// than appearing frozen during the exponential backoff.
    /// </summary>
    public int RetryDelaySeconds { get; init; }
}
