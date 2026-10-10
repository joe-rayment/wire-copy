// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs;

/// <summary>
/// Result of a TTS audio generation operation.
/// </summary>
public record TtsGenerationResult
{
    /// <summary>
    /// Gets whether the generation completed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the generated audio data as a byte array. Null on failure.
    /// </summary>
    public byte[]? AudioData { get; init; }

    /// <summary>
    /// Gets the total number of characters processed.
    /// </summary>
    public int CharactersProcessed { get; init; }

    /// <summary>
    /// Gets the number of chunks that completed successfully.
    /// </summary>
    public int ChunksCompleted { get; init; }

    /// <summary>
    /// Gets the optional error message if generation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets whether the failure is run-ending rather than per-article: bad/revoked credentials
    /// (401/403) or exhausted billing quota. Every remaining article would fail identically, so
    /// the orchestrator stops synthesizing on the first fatal failure. False for ordinary,
    /// possibly article-specific failures (workspace-y3eg7).
    /// </summary>
    public bool IsFatal { get; init; }

    /// <summary>
    /// Creates a successful generation result.
    /// </summary>
    public static TtsGenerationResult Successful(byte[] audioData, int charactersProcessed, int chunksCompleted)
    {
        return new TtsGenerationResult
        {
            Success = true,
            AudioData = audioData,
            CharactersProcessed = charactersProcessed,
            ChunksCompleted = chunksCompleted,
        };
    }

    /// <summary>
    /// Creates a failed generation result.
    /// </summary>
    public static TtsGenerationResult Failure(string errorMessage, int charactersProcessed = 0, int chunksCompleted = 0, bool isFatal = false)
    {
        return new TtsGenerationResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            CharactersProcessed = charactersProcessed,
            ChunksCompleted = chunksCompleted,
            IsFatal = isFatal,
        };
    }
}
