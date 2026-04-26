// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.DTOs;

namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for generating audio from text using a TTS provider.
/// </summary>
public interface ITtsService
{
    /// <summary>
    /// Gets whether the TTS service is configured with valid credentials.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Estimates the cost and duration for generating audio from the given text.
    /// </summary>
    /// <param name="text">The text to estimate for.</param>
    /// <returns>A cost estimate with character count, chunk count, and projected cost.</returns>
    TtsCostEstimate EstimateCost(string text);

    /// <summary>
    /// Validates the current API key by making a minimal test call.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A validation result indicating whether the key is valid.</returns>
    Task<TtsValidationResult> ValidateApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a runtime API key override, allowing the UI to inject credentials.
    /// </summary>
    /// <param name="apiKey">The API key to use.</param>
    void SetApiKeyOverride(string apiKey);

    /// <summary>
    /// Generates audio from text, reporting progress per chunk.
    /// </summary>
    /// <param name="text">The text to convert to audio.</param>
    /// <param name="title">The title used for logging and metadata.</param>
    /// <param name="progress">Optional progress callback invoked after each chunk completes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generation result containing audio data or an error message.</returns>
    Task<TtsGenerationResult> GenerateAudioAsync(
        string text,
        string title,
        IProgress<TtsProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
