// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Animations;

/// <summary>
/// A thin wrapper around <see cref="DecryptRevealAnimation"/> that re-targets the
/// 8-frame decrypt-reveal sweep at an arbitrary screen row in the theme's
/// <see cref="ThemePalette.GetAccentFg"/> color.
/// Used to flash the saved row after a successful save-to-Reading-List action.
/// AccentFg (cyan in the design system) is used deliberately — CelebrationFg
/// is reserved for one-time celebrations only.
/// </summary>
internal static class SaveFlashAnimation
{
    /// <summary>
    /// Plays the save-flash animation for the given row text at the given screen
    /// position. Synchronous blocking call (~400ms total). Safe to call from any
    /// thread — console access is guarded by <see cref="ConsoleSync.Lock"/>.
    /// No-op when <paramref name="text"/> is null/empty.
    /// </summary>
    /// <param name="text">The text to flash (typically the saved link's display title).</param>
    /// <param name="row">Console row where the text is rendered.</param>
    /// <param name="col">Console column where the text begins.</param>
    /// <param name="palette">Theme palette for color selection.</param>
    public static void PlayRow(string text, int row, int col, ThemePalette palette)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Bold accent color — matches the AccentFg role in the design system.
        var revealColor = $"{palette.GetAccentFg().AnsiFg}\x1b[1m";
        DecryptRevealAnimation.Play(text, row, col, palette, revealColor);
    }
}
