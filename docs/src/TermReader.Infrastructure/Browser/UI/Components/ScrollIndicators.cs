// Educational and personal use only.

using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared scroll indicators (▲/▼) for views with scrollable content.
/// </summary>
internal static class ScrollIndicators
{
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Renders a "more above" scroll indicator centered in the width.
    /// </summary>
    public static string MoreAbove(ThemePalette p, int count, int width)
    {
        var text = $"\u25b2 {count} more above";
        var pad = Math.Max(0, (width - text.Length) / 2);
        return $"{new string(' ', pad)}{p.SecondaryText.AnsiFg}{text}{Reset}";
    }

    /// <summary>
    /// Renders a "more below" scroll indicator centered in the width.
    /// </summary>
    public static string MoreBelow(ThemePalette p, int count, int width)
    {
        var text = $"\u25bc {count} more below";
        var pad = Math.Max(0, (width - text.Length) / 2);
        return $"{new string(' ', pad)}{p.SecondaryText.AnsiFg}{text}{Reset}";
    }
}
