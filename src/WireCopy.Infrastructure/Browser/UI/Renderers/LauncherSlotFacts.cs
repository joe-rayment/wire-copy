// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// The per-tile facts of one launcher slot, shared between the Grid card render
/// (<see cref="LauncherRenderer.ComposeSlot"/> consumers) and the shell-channel
/// view-model export so the two share one composition (workspace-0jxo).
/// <see cref="Title"/> is the display name (upper-cased for bookmarks);
/// <see cref="Hint"/> is the subtitle (a bookmark's domain, or the Reading List
/// saved-articles copy); <see cref="Url"/> is the bookmark href (null for the
/// Reading List slot).
/// </summary>
internal readonly record struct LauncherSlotFacts(
    bool IsReadingList, string Title, string Hint, string? Url);
