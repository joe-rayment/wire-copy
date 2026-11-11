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
    /// Gets a value indicating whether an audio-engine URL is present (the API key is optional).
    /// Cheap: reads settings only, never touches the network.
    /// </summary>
    bool IsConfigured { get; }

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

    /// <summary>Polls the job every 2 s while its publication is pending; gives up after 10 minutes.</summary>
    /// <param name="jobId">The audio-engine job id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last publication state (still pending on timeout); null when there is none or audio-engine can't be asked.</returns>
    Task<NarrationPublication?> WaitForPublicationAsync(string jobId, CancellationToken ct = default);

    /// <summary>Asks audio-engine which feed is its default.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The default feed; null when none is set or audio-engine is unreachable.</returns>
    Task<NarrationFeed?> GetDefaultFeedAsync(CancellationToken ct = default);
}
