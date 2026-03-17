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
                SecondaryText = new ThemeColor(ConsoleColor.DarkGreen, 34),   // #00af00 dimmer for contrast
                LinkContent = new ThemeColor(ConsoleColor.Green, 48),         // slightly brighter for headlines
                LinkNavigation = new ThemeColor(ConsoleColor.DarkGreen, 40),
                LinkExternal = new ThemeColor(ConsoleColor.DarkGreen, 40),
                LinkFooter = new ThemeColor(ConsoleColor.DarkGreen, 40),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkGreen, 35),  // subtle border green
                HeaderTitleFg = new ThemeColor(ConsoleColor.Green, 48),       // brighter headline green
                HeaderUrlFg = new ThemeColor(ConsoleColor.DarkGreen, 34),
                SelectedItemFg = new ThemeColor(ConsoleColor.White, 15),
                SelectedItemBg = new ThemeColor(ConsoleColor.DarkGreen, 22),  // dark green selection
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkGreen, 35),
                StatusBarTextFg = new ThemeColor(ConsoleColor.Green, 46),
                PromptFg = new ThemeColor(ConsoleColor.Green, 46),
                PromptLabelFg = new ThemeColor(ConsoleColor.DarkGreen, 34),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 203),
                SearchHighlightFg = new ThemeColor(ConsoleColor.Black, 0),
                SearchHighlightBg = new ThemeColor(ConsoleColor.Green, 46),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkGreen, 34),
                FocusIndicatorFg = new ThemeColor(ConsoleColor.DarkGreen, 22),
                AccentFg = new ThemeColor(ConsoleColor.Cyan, 43),            // cyan accent for interactive hints
                DimFg = new ThemeColor(ConsoleColor.DarkGreen, 28),          // very dim green for decorative elements
            },
            [ThemeName.Amber] = new ThemePalette
            {
                PrimaryText = new ThemeColor(ConsoleColor.Yellow, 220),       // warm amber
                SecondaryText = new ThemeColor(ConsoleColor.DarkYellow, 136),  // dim amber
                LinkContent = new ThemeColor(ConsoleColor.Yellow, 221),        // slightly warmer headlines
                LinkNavigation = new ThemeColor(ConsoleColor.DarkYellow, 136),
                LinkExternal = new ThemeColor(ConsoleColor.DarkYellow, 136),
                LinkFooter = new ThemeColor(ConsoleColor.DarkYellow, 130),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkYellow, 130), // deeper amber for borders
                HeaderTitleFg = new ThemeColor(ConsoleColor.Yellow, 221),
                HeaderUrlFg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                SelectedItemFg = new ThemeColor(ConsoleColor.Black, 0),
                SelectedItemBg = new ThemeColor(ConsoleColor.Yellow, 220),
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkYellow, 130),
                StatusBarTextFg = new ThemeColor(ConsoleColor.Yellow, 220),
                PromptFg = new ThemeColor(ConsoleColor.Yellow, 220),
                PromptLabelFg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 203),
                SearchHighlightFg = new ThemeColor(ConsoleColor.Black, 0),
                SearchHighlightBg = new ThemeColor(ConsoleColor.Yellow, 220),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkYellow, 94),      // darker amber for read items
                FocusIndicatorFg = new ThemeColor(ConsoleColor.DarkYellow, 94),
                AccentFg = new ThemeColor(ConsoleColor.Yellow, 208),           // warm orange accent
                DimFg = new ThemeColor(ConsoleColor.DarkYellow, 94),           // very dim amber
            },
            [ThemeName.Dracula] = new ThemePalette
            {
                PrimaryText = new ThemeColor(ConsoleColor.White, 253),        // foreground (slightly brighter)
                SecondaryText = new ThemeColor(ConsoleColor.Gray, 245),       // comment
                LinkContent = new ThemeColor(ConsoleColor.Cyan, 80),          // cyan
                LinkNavigation = new ThemeColor(ConsoleColor.Gray, 245),
                LinkExternal = new ThemeColor(ConsoleColor.Gray, 245),
                LinkFooter = new ThemeColor(ConsoleColor.Gray, 240),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkMagenta, 98),  // deeper purple for borders
                HeaderTitleFg = new ThemeColor(ConsoleColor.Magenta, 212),      // pink
                HeaderUrlFg = new ThemeColor(ConsoleColor.Gray, 245),
                SelectedItemFg = new ThemeColor(ConsoleColor.White, 253),
                SelectedItemBg = new ThemeColor(ConsoleColor.DarkGray, 237),    // selection
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkGray, 238),
                StatusBarTextFg = new ThemeColor(ConsoleColor.Cyan, 80),
                PromptFg = new ThemeColor(ConsoleColor.Green, 114),
                PromptLabelFg = new ThemeColor(ConsoleColor.Yellow, 220),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 203),
                SearchHighlightFg = new ThemeColor(ConsoleColor.Black, 0),
                SearchHighlightBg = new ThemeColor(ConsoleColor.Yellow, 220),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                FocusIndicatorFg = new ThemeColor(ConsoleColor.DarkGray, 238),
                AccentFg = new ThemeColor(ConsoleColor.Cyan, 80),              // cyan accent (matches link color)
                DimFg = new ThemeColor(ConsoleColor.DarkGray, 236),            // very dim gray
            },
            [ThemeName.Light] = new ThemePalette
            {
                PrimaryText = new ThemeColor(ConsoleColor.Black, 235),
                SecondaryText = new ThemeColor(ConsoleColor.DarkGray, 240),
                LinkContent = new ThemeColor(ConsoleColor.DarkBlue, 25),
                LinkNavigation = new ThemeColor(ConsoleColor.DarkGray, 240),
                LinkExternal = new ThemeColor(ConsoleColor.DarkGray, 240),
                LinkFooter = new ThemeColor(ConsoleColor.DarkGray, 245),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkGray, 242),   // slightly lighter borders
                HeaderTitleFg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                HeaderUrlFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                SelectedItemFg = new ThemeColor(ConsoleColor.White, 252),
                SelectedItemBg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkGray, 242),
                StatusBarTextFg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                PromptFg = new ThemeColor(ConsoleColor.DarkBlue, 25),
                PromptLabelFg = new ThemeColor(ConsoleColor.DarkGray, 240),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 160),
                SearchHighlightFg = new ThemeColor(ConsoleColor.White, 252),
                SearchHighlightBg = new ThemeColor(ConsoleColor.DarkYellow, 136),
                ReadItemFg = new ThemeColor(ConsoleColor.DarkGray, 245),
                FocusIndicatorFg = new ThemeColor(ConsoleColor.Gray, 249),
                AccentFg = new ThemeColor(ConsoleColor.DarkBlue, 32),          // lighter blue accent
                DimFg = new ThemeColor(ConsoleColor.Gray, 248),                // light gray for decoration
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
