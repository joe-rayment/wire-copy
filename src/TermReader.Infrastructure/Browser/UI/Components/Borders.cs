// Educational and personal use only.

using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Renderers;

namespace TermReader.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared border and box-drawing components used across all renderers.
/// </summary>
internal static class Borders
{
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Renders a rounded box header (╭─╮│text│╰─╯).
    /// </summary>
    public static void RenderRoundedBox(RenderHelpers helpers, ThemePalette p, string title, int width)
    {
        helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u256d{new string('\u2500', width - 2)}\u256e{Reset}");
        var displayTitle = RenderHelpers.TruncateText(title, width - 4);
        helpers.WriteLine(
            $"{p.HeaderBorderFg.AnsiFg}\u2502 {p.HeaderTitleFg.AnsiFg}" +
            $"{displayTitle.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} \u2502{Reset}");
        helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{Reset}");
    }

    /// <summary>
    /// Renders a horizontal rule spanning the given width.
    /// </summary>
    public static string HorizontalRule(ThemePalette p, int width)
    {
        return $"{p.HeaderBorderFg.AnsiFg}{new string('\u2500', Math.Max(1, width))}{Reset}";
    }

    /// <summary>
    /// Renders a dimmed horizontal rule (for card separators).
    /// </summary>
    public static string DimmedRule(ThemePalette p, int width)
    {
        return $"{p.SecondaryText.AnsiFg}\x1b[2m{new string('\u2500', Math.Max(1, width))}{Reset}";
    }

    /// <summary>
    /// Renders a short separator between list items (╴──╶).
    /// </summary>
    public static string ItemSeparator(ThemePalette p, int indent = 3)
    {
        return $"{new string(' ', indent)}{p.SecondaryText.AnsiFg}\u2576\u2500\u2500\u2574{Reset}";
    }

    /// <summary>
    /// Renders a vertical column divider character.
    /// </summary>
    public static string ColumnDivider(ThemePalette p)
    {
        return $"{p.SecondaryText.AnsiFg}\u2502{Reset}";
    }
}
