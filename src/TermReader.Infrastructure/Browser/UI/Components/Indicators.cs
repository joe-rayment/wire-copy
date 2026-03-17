// Educational and personal use only.

using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared visual indicator characters and formatters.
/// </summary>
internal static class Indicators
{
    /// <summary>Filled circle — unread/active state.</summary>
    public const char FilledCircle = '\u25cf';

    /// <summary>Empty circle — read/inactive state.</summary>
    public const char EmptyCircle = '\u25cb';

    /// <summary>Collapse indicator: expanded (down-pointing triangle).</summary>
    public const char Expanded = '\u25bc';

    /// <summary>Collapse indicator: collapsed (right-pointing triangle).</summary>
    public const char Collapsed = '\u25b6';

    /// <summary>Selection arrow.</summary>
    public const char SelectionArrow = '\u2192';

    /// <summary>Checkmark.</summary>
    public const char Checkmark = '\u2714';

    /// <summary>Star marker for default collections.</summary>
    public const char Star = '\u2605';

    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Renders a filled progress bar segment.
    /// </summary>
    public static string ProgressBar(ThemePalette p, int filled, int total)
    {
        var empty = Math.Max(0, total - filled);
        return $"{p.PromptFg.AnsiFg}{new string('\u25b0', filled)}" +
               $"{p.SecondaryText.AnsiFg}{new string('\u25b1', empty)}{Reset}";
    }
}
