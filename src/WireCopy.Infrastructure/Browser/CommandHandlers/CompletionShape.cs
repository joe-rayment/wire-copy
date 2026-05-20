// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Typed completion-screen shape (workspace-n49i Phase 4). Drives both
/// the headline glyph (✓ / ⚠) and which content blocks render in
/// <see cref="PodcastProgressScreens.BuildCompletionLines"/>. Maps to
/// the bead's documented shapes:
/// <list type="bullet">
///   <item><see cref="FullSuccess"/> — Shape A: ✓ "Podcast generated and published".</item>
///   <item><see cref="LocalOnlySuccess"/> — Shape B: ✓ "...local-only — no RSS publish".</item>
///   <item><see cref="PartialFailure"/> — Shape C: ⚠ "...with N article failures".</item>
/// </list>
/// </summary>
internal enum CompletionShape
{
    /// <summary>Shape A — file written, feed published, no article failures.</summary>
    FullSuccess,

    /// <summary>Shape B — file written, no feed (no GCS bucket configured), no failures.</summary>
    LocalOnlySuccess,

    /// <summary>Shape C — at least one article failed; file may or may not be present.</summary>
    PartialFailure,
}
