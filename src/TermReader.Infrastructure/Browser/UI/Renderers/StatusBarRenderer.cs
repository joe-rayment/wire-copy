// Educational and personal use only.

using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the general-purpose status bar for hierarchical and readable views.
/// Uses colored mode labels and key hints with consistent styling.
/// </summary>
internal class StatusBarRenderer
{
    private const string Reset = "\x1b[0m";
    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public StatusBarRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    public void RenderStatusBar(NavigationContext context, ViewMode mode)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var themeName = _themeProvider.CurrentTheme.ToString();

        _helpers.WriteLine();
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        _helpers.WriteLine($"{p.StatusBarSeparatorFg.AnsiFg}{new string('\u2500', separatorWidth)}{Reset}");

        var modeLabel = GetModeLabel(mode);
        var hints = GetKeyHints(mode, p);
        var search = !string.IsNullOrEmpty(context.SearchQuery) ? $" {p.SecondaryText.AnsiFg}|{Reset} {p.PromptFg.AnsiFg}/{context.SearchQuery}{Reset} {p.SecondaryText.AnsiFg}(n/N){Reset}" : string.Empty;
        var back = context.CanGoBack ? $"{p.SecondaryText.AnsiFg}[\u2190back]{Reset} " : string.Empty;

        _helpers.WriteLine($"{back}{p.StatusBarTextFg.AnsiFg}{modeLabel} | {themeName}{Reset} {hints}{search}");
    }

    private static string GetModeLabel(ViewMode mode)
    {
        return mode switch
        {
            ViewMode.Hierarchical => "LinkView",
            ViewMode.Readable => "ReaderView",
            ViewMode.CollectionList => "Collections",
            ViewMode.CollectionItems => "Items",
            ViewMode.Launcher => "Launcher",
            _ => "Browser"
        };
    }

    private static string GetKeyHints(ViewMode mode, ThemePalette p)
    {
        return mode switch
        {
            ViewMode.Hierarchical => FormatHints(
                p,
                ("j/k", "move"),
                ("Enter", "select"),
                ("v", "reader"),
                ("?", "shortcuts")),
            ViewMode.Readable => FormatHints(
                p,
                ("j/k", "scroll"),
                ("v", "links"),
                ("b", "back"),
                ("?", "shortcuts")),
            ViewMode.CollectionList => FormatHints(
                p,
                ("j/k", "move"),
                ("Enter", "open"),
                ("?", "shortcuts")),
            ViewMode.CollectionItems => FormatHints(
                p,
                ("j/k", "move"),
                ("Enter", "open"),
                ("b", "back"),
                ("?", "shortcuts")),
            ViewMode.Launcher => FormatHints(
                p,
                ("hjkl", "navigate"),
                ("Enter", "open"),
                ("?", "shortcuts")),
            _ => $"{p.SecondaryText.AnsiFg}q:quit{Reset}"
        };
    }

    private static string FormatHints(ThemePalette p, params (string Key, string Action)[] hints)
    {
        return string.Join(" ", hints.Select(h =>
            $"{p.PrimaryText.AnsiFg}{h.Key}{Reset}{p.SecondaryText.AnsiFg}:{h.Action}{Reset}"));
    }
}
