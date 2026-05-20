// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Explicit classification of a <see cref="PodcastResult"/> for the
/// result screen (workspace-3a2k Phase E). Set by the orchestrator so the
/// screen layer doesn't have to infer state by inspecting nullable fields.
/// </summary>
public enum PodcastResultClassification
{
    /// <summary>Pipeline completed; every article shipped and the published feed is publicly reachable.</summary>
    FullSuccess = 0,

    /// <summary>Pipeline completed but at least one article failed extraction/TTS, or no feed was published (local-only run).</summary>
    PartialSuccess = 1,

    /// <summary>Pipeline failed before producing a usable result — or produced a feed the public internet can't read.</summary>
    TotalFailure = 2,
}
