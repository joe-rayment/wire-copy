// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;

namespace WireCopy.Application.Interfaces.Podcast;

/// <summary>
/// Turns text into an m4b audiobook by calling the audio-engine service. Replaces the in-process
/// TTS pipeline. User-facing failure messages always start with "audio-engine" so
/// <c>PodcastFailureClassifier</c> can route them.
/// </summary>
public interface IPodcastNarrator
{
    /// <summary>
    /// Gets a value indicating whether an audio-engine URL and API key are both present.
    /// Cheap: reads settings only, never touches the network.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets the engine the current settings select: "chatterbox" when the TtsEngine setting says so,
    /// otherwise "openai".
    /// </summary>
    string EngineName { get; }

    /// <summary>Checks that audio-engine is reachable, accepts the key and has the selected engine configured.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The status; never throws for an expected failure.</returns>
    Task<NarratorStatus> CheckAsync(CancellationToken ct = default);

    /// <summary>Prices a request (cost, cache hits, duration, budget) without creating a job.</summary>
    /// <param name="request">What would be narrated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The estimate; never throws for an expected failure.</returns>
    Task<NarrationEstimate> EstimateAsync(NarrationRequest request, CancellationToken ct = default);

    /// <summary>Narrates the request and downloads the m4b to <see cref="NarrationRequest.OutputPath"/>.</summary>
    /// <param name="request">What to narrate.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token; cancelling also asks audio-engine to cancel the job.</param>
    /// <returns>The result; never throws for an expected failure (only for cancellation).</returns>
    Task<NarrationResult> NarrateAsync(NarrationRequest request, IProgress<NarrationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Narrates a short sample with the current voice settings to <paramref name="outputPath"/>.</summary>
    /// <param name="text">The sample text.</param>
    /// <param name="outputPath">Where the audio is written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result; never throws for an expected failure (only for cancellation).</returns>
    Task<NarrationResult> NarrateTestAsync(string text, string outputPath, CancellationToken ct = default);
}
