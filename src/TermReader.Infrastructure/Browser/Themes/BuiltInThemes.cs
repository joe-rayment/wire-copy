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
                // Phosphor 2.0: bright green is primary/dominant, pink is accent only
                PrimaryText = new ThemeColor(ConsoleColor.Green, 40),         // #00d700 pure phosphor green
                SecondaryText = new ThemeColor(ConsoleColor.DarkGreen, 108),  // #87af87 sage green, readable on dark bg
                LinkContent = new ThemeColor(ConsoleColor.Green, 40),         // #00d700 article links match primary
                LinkNavigation = new ThemeColor(ConsoleColor.DarkGreen, 108), // sage green for nav links
                LinkExternal = new ThemeColor(ConsoleColor.DarkGreen, 108),
                LinkFooter = new ThemeColor(ConsoleColor.DarkGreen, 108),
                HeaderBorderFg = new ThemeColor(ConsoleColor.DarkGreen, 22),  // #005f00 dark green structural
                HeaderTitleFg = new ThemeColor(ConsoleColor.Green, 40),       // #00d700 bright green titles
                HeaderUrlFg = new ThemeColor(ConsoleColor.DarkGreen, 108),
                SelectedItemFg = new ThemeColor(ConsoleColor.White, 254),     // #e4e4e4 warm off-white
                SelectedItemBg = new ThemeColor(ConsoleColor.DarkGreen, 22),  // #005f00 dark green selection
                StatusBarSeparatorFg = new ThemeColor(ConsoleColor.DarkGreen, 22),
                StatusBarTextFg = new ThemeColor(ConsoleColor.Green, 40),     // #00d700 phosphor green
                PromptFg = new ThemeColor(ConsoleColor.Green, 40),
                PromptLabelFg = new ThemeColor(ConsoleColor.DarkGreen, 108),
                ErrorFg = new ThemeColor(ConsoleColor.Red, 203),              // #ff5f5f
                SearchHighlightFg = new ThemeColor(ConsoleColor.White, 15),   // white on blue-teal
                SearchHighlightBg = new ThemeColor(ConsoleColor.DarkCyan, 24), // #005f87 blue-teal highlight
                ReadItemFg = new ThemeColor(ConsoleColor.DarkGreen, 65),      // #5f875f muted aged phosphor
                FocusIndicatorFg = new ThemeColor(ConsoleColor.DarkGreen, 65), // #5f875f visible muted
                AccentFg = new ThemeColor(ConsoleColor.Cyan, 51),             // #00ffff bright cyan interactive
                DimFg = new ThemeColor(ConsoleColor.DarkGreen, 22),           // #005f00 structural chrome
                MutedFg = new ThemeColor(ConsoleColor.DarkGreen, 65),         // #5f875f quiet text
                SuccessFg = new ThemeColor(ConsoleColor.Green, 119),          // #87ff5f warm bright success
                CelebrationFg = new ThemeColor(ConsoleColor.Magenta, 206),    // #ff5fd7 vivid hot pink (accent)
                WarningFg = new ThemeColor(ConsoleColor.Yellow, 214),         // #ffaf00 amber warnings
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
                MutedFg = new ThemeColor(ConsoleColor.DarkYellow, 94),         // dark amber for quiet text
                SuccessFg = new ThemeColor(ConsoleColor.Yellow, 220),          // bright amber for success
                CelebrationFg = new ThemeColor(ConsoleColor.Yellow, 208),      // warm orange celebration
                WarningFg = new ThemeColor(ConsoleColor.Red, 196),             // red for warnings
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
                MutedFg = new ThemeColor(ConsoleColor.DarkGray, 242),          // muted gray
                SuccessFg = new ThemeColor(ConsoleColor.Green, 114),           // Dracula green
                CelebrationFg = new ThemeColor(ConsoleColor.Magenta, 212),     // Dracula pink
                WarningFg = new ThemeColor(ConsoleColor.Yellow, 220),          // Dracula yellow
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
                MutedFg = new ThemeColor(ConsoleColor.Gray, 246),              // medium gray
                SuccessFg = new ThemeColor(ConsoleColor.DarkGreen, 28),        // dark green success
                CelebrationFg = new ThemeColor(ConsoleColor.DarkMagenta, 162), // dark magenta celebration
                WarningFg = new ThemeColor(ConsoleColor.DarkYellow, 172),      // dark amber warning
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
