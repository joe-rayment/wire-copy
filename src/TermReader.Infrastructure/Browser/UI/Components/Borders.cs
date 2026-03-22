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
    /// Renders a rounded box header with title and subtitle line.
    /// ╭─ Title ────────────────────────────────────╮
    /// │ subtitle text                              │
    /// ╰────────────────────────────────────────────╯
    /// </summary>
    public static void RenderRoundedBoxWithSubtitle(
        RenderHelpers helpers, ThemePalette p, string title, string subtitle, int width)
    {
        var displayTitle = RenderHelpers.TruncateText(title, width - 6);
        var titleRule = $"\u2500 {displayTitle} ";
        var remainingRule = Math.Max(0, width - 2 - titleRule.Length);
        helpers.WriteLine(
            $"{p.HeaderBorderFg.AnsiFg}\u256d{titleRule}" +
            $"{new string('\u2500', remainingRule)}\u256e{Reset}");
        var displaySubtitle = RenderHelpers.TruncateText(subtitle, width - 4);
        helpers.WriteLine(
            $"{p.HeaderBorderFg.AnsiFg}\u2502 {p.SecondaryText.AnsiFg}" +
            $"{displaySubtitle.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} \u2502{Reset}");
        helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{Reset}");
    }

    /// <summary>
    /// Renders a section separator with a title embedded in a rounded top corner.
    /// ╭─ Section Name (count) ────────╮
    /// Used for link tree group headers as an open-top border.
    /// </summary>
    public static string SectionHeader(ThemePalette p, string title, int width)
    {
        var displayTitle = RenderHelpers.TruncateText(title, width - 6);
        var titlePart = $"\u256d\u2500 {displayTitle} ";
        var remainingRule = Math.Max(0, width - titlePart.Length - 1);
        return $"{p.HeaderBorderFg.AnsiFg}{titlePart}{new string('\u2500', remainingRule)}\u256e{Reset}";
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
        return $"{p.GetDimFg().AnsiFg}\x1b[2m{new string('\u2500', Math.Max(1, width))}{Reset}";
    }

    /// <summary>
    /// Renders a short separator between list items (╴──╶).
    /// </summary>
    public static string ItemSeparator(ThemePalette p, int indent = 3)
    {
        return $"{new string(' ', indent)}{p.GetDimFg().AnsiFg}\u2576\u2500\u2500\u2574{Reset}";
    }

    /// <summary>
    /// Renders a vertical column divider character.
    /// </summary>
    public static string ColumnDivider(ThemePalette p)
    {
        return $"{p.GetDimFg().AnsiFg}\u2502{Reset}";
    }
}
