// Educational and personal use only.

using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared selection highlight and accent bar components.
/// </summary>
internal static class Selection
{
    /// <summary>
    /// The accent bar character (▌) shown on the left of selected items.
    /// </summary>
    public const char AccentBarChar = '\u258c';

    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Renders a left accent bar in the given color.
    /// </summary>
    public static string AccentBar(ThemePalette p)
    {
        return $"{p.HeaderBorderFg.AnsiFg}{AccentBarChar}{Reset}";
    }

    /// <summary>
    /// Renders a left accent bar with the selected item foreground color.
    /// </summary>
    public static string SelectedAccentBar(ThemePalette p)
    {
        return $"{p.SelectedItemFg.AnsiFg}{AccentBarChar}{Reset}";
    }

    /// <summary>
    /// Wraps text with selection background and foreground colors.
    /// </summary>
    public static string Highlight(ThemePalette p, string text)
    {
        return $"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg}{text}{Reset}";
    }

    /// <summary>
    /// Wraps text with reverse video (swaps fg/bg).
    /// </summary>
    public static string ReverseVideo(string text)
    {
        return $"\x1b[7m{text}\x1b[27m{Reset}";
    }
}
