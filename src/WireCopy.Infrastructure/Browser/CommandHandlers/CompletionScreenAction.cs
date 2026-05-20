// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Result-screen action returned from
/// <see cref="PodcastProgressScreens.ShowCompletionScreenAsync"/> to the
/// caller (PodcastCommandHandler). <see cref="Back"/> = user pressed
/// Enter/Esc, return to library; <see cref="Retry"/> = user pressed
/// <c>r</c> on the completion screen and wants to re-run the generation
/// flow from scratch (workspace-n49i Phase 4).
/// </summary>
internal enum CompletionScreenAction
{
    /// <summary>User pressed Enter or Esc — return to the previous view.</summary>
    Back,

    /// <summary>User pressed <c>r</c> — re-run the generation flow from scratch.</summary>
    Retry,
}
