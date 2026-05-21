// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Podcast;

/// <summary>
/// Current phase of work within a Running PodcastJob. Lets the resume path
/// (workspace-ur2s, F.4) skip already-completed phases without redoing
/// extraction or TTS. Append-only enum.
/// </summary>
public enum PodcastJobPhase
{
    /// <summary>Not yet started any work.</summary>
    NotStarted = 0,

    /// <summary>Loading articles from collection items.</summary>
    Extraction = 1,

    /// <summary>OpenAI TTS synthesis (per-article + per-chunk).</summary>
    Synthesis = 2,

    /// <summary>FFmpeg M4B assembly + faststart atom.</summary>
    Assembly = 3,

    /// <summary>GCS upload + feed.xml + manifest + reachability probe.</summary>
    Publish = 4,

    /// <summary>Terminal — no more phases to advance to.</summary>
    Done = 5,
}
