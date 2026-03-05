// Educational and personal use only.

using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.Themes;

/// <summary>
/// Static factory providing built-in theme palettes.
/// Tile borders reuse HeaderBorderFg, tile text reuses PrimaryText/SecondaryText,
/// tile selection reuses SelectedItemFg/Bg.
/// </summary>
public static class BuiltInThemes
{
    private static readonly IReadOnlyDictionary<ThemeName, ThemePalette> Palettes =
        new Dictionary<ThemeName, ThemePalette>
        {
            [ThemeName.Phosphor] = new ThemePalette
            {
                PrimaryText = new ThemeColor(ConsoleColor.Green, 46),        // #00ff00 classic CRT
                SecondaryText = new ThemeColor(ConsoleColor.DarkGreen, 28),   // #008700 dim CRT
                LinkContent = new ThemeColor(ConsoleColor.Green, 46),
                LinkNavigation = new ThemeColor(ConsoleColor.DarkGreen, 28),
                LinkExternal = new ThemeColor(ConsoleColor.DarkGreen, 28),
                LinkFooter = new ThemeColor(ConsoleColor.DarkGreen, 28),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkGreen, 28),
                HeaderTitleFg = new ThemeColor(ConsoleColor.Green, 46),
                HeaderUrlFg = new ThemeColor(ConsoleColor.DarkGreen, 28),
                SelectedItemFg = new ThemeColor(ConsoleColor.Black, 0),
                SelectedItemBg = new ThemeColor(ConsoleColor.Green, 46),
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkGreen, 28),
                StatusBarTextFg = new ThemeColor(ConsoleColor.Green, 46),
                PromptFg = new ThemeColor(ConsoleColor.Green, 46),
                PromptLabelFg = new ThemeColor(ConsoleColor.DarkGreen, 28),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 203),
                SearchHighlightFg = new ThemeColor(ConsoleColor.Black, 0),
                SearchHighlightBg = new ThemeColor(ConsoleColor.Green, 46),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkGreen, 28),
            },
            [ThemeName.Amber] = new ThemePalette
            {
                PrimaryText = new ThemeColor(ConsoleColor.Yellow, 220),       // amber
                SecondaryText = new ThemeColor(ConsoleColor.DarkYellow, 136),  // dim amber
                LinkContent = new ThemeColor(ConsoleColor.Yellow, 220),
                LinkNavigation = new ThemeColor(ConsoleColor.DarkYellow, 136),
                LinkExternal = new ThemeColor(ConsoleColor.DarkYellow, 136),
                LinkFooter = new ThemeColor(ConsoleColor.DarkYellow, 136),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                HeaderTitleFg = new ThemeColor(ConsoleColor.Yellow, 220),
                HeaderUrlFg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                SelectedItemFg = new ThemeColor(ConsoleColor.Black, 0),
                SelectedItemBg = new ThemeColor(ConsoleColor.Yellow, 220),
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                StatusBarTextFg = new ThemeColor(ConsoleColor.Yellow, 220),
                PromptFg = new ThemeColor(ConsoleColor.Yellow, 220),
                PromptLabelFg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 203),
                SearchHighlightFg = new ThemeColor(ConsoleColor.Black, 0),
                SearchHighlightBg = new ThemeColor(ConsoleColor.Yellow, 220),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkYellow, 136),
            },
            [ThemeName.Dracula] = new ThemePalette
            {
                PrimaryText = new ThemeColor(ConsoleColor.White, 252),        // foreground
                SecondaryText = new ThemeColor(ConsoleColor.Gray, 245),       // comment
                LinkContent = new ThemeColor(ConsoleColor.Cyan, 80),          // cyan
                LinkNavigation = new ThemeColor(ConsoleColor.Gray, 245),
                LinkExternal = new ThemeColor(ConsoleColor.Gray, 245),
                LinkFooter = new ThemeColor(ConsoleColor.Gray, 240),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkMagenta, 141), // purple
                HeaderTitleFg = new ThemeColor(ConsoleColor.Magenta, 212),      // pink
                HeaderUrlFg = new ThemeColor(ConsoleColor.Gray, 245),
                SelectedItemFg = new ThemeColor(ConsoleColor.White, 252),
                SelectedItemBg = new ThemeColor(ConsoleColor.DarkGray, 237),    // selection
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                StatusBarTextFg = new ThemeColor(ConsoleColor.Cyan, 80),
                PromptFg = new ThemeColor(ConsoleColor.Green, 114),
                PromptLabelFg = new ThemeColor(ConsoleColor.Yellow, 220),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 203),
                SearchHighlightFg = new ThemeColor(ConsoleColor.Black, 0),
                SearchHighlightBg = new ThemeColor(ConsoleColor.Yellow, 220),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkGray, 240),
            },
            [ThemeName.Light] = new ThemePalette
            {
                PrimaryText = new ThemeColor(ConsoleColor.Black, 235),
                SecondaryText = new ThemeColor(ConsoleColor.DarkGray, 240),
                LinkContent = new ThemeColor(ConsoleColor.DarkBlue, 25),
                LinkNavigation = new ThemeColor(ConsoleColor.DarkGray, 240),
                LinkExternal = new ThemeColor(ConsoleColor.DarkGray, 240),
                LinkFooter = new ThemeColor(ConsoleColor.DarkGray, 245),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                HeaderTitleFg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                HeaderUrlFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                SelectedItemFg = new ThemeColor(ConsoleColor.White, 252),
                SelectedItemBg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                StatusBarTextFg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                PromptFg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                PromptLabelFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 160),
                SearchHighlightFg = new ThemeColor(ConsoleColor.White, 252),
                SearchHighlightBg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkGray, 245),
            },
        };

    /// <summary>
    /// Returns the theme palette for the given theme name.
    /// </summary>
    public static ThemePalette Get(ThemeName theme)
    {
        return Palettes[theme];
    }
}
