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
        var search = !string.IsNullOrEmpty(context.SearchQuery) ? $" {p.SecondaryText.AnsiFg}|{Reset} {p.PromptFg.AnsiFg}/{context.SearchQuery}{Reset} {p.SecondaryText.AnsiFg}(n/N){Reset}" : "";
        var back = context.CanGoBack ? $"{p.SecondaryText.AnsiFg}[\u2190back]{Reset} " : "";

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
            ViewMode.Hierarchical => FormatHints(p,
                ("j/k", "move"), ("h", "collapse"), ("l", "expand"),
                ("Enter", "select"), ("v", "reader"), ("/", "search"),
                (":", "cmd"), ("q", "quit")),
            ViewMode.Readable => FormatHints(p,
                ("j/k", "scroll"), ("v", "links"), ("b", "back"),
                ("/", "search"), (":", "cmd"), ("q", "quit")),
            ViewMode.CollectionList => FormatHints(p,
                ("j/k", "move"), ("Enter", "open"), ("s", "set-default"),
                ("d", "delete"), (":new", "create"), ("q", "quit")),
            ViewMode.CollectionItems => FormatHints(p,
                ("j/k", "move"), ("Enter", "open"), ("d", "remove"),
                ("J/K", "reorder"), ("b", "back"), (":export", "export"),
                ("q", "quit")),
            ViewMode.Launcher => FormatHints(p,
                ("h/j/k/l", "navigate"), ("Enter", "open"), ("a", "add"),
                ("d", "delete"), ("c", "collections"), (":", "cmd"),
                ("q", "quit")),
            _ => $"{p.SecondaryText.AnsiFg}q:quit{Reset}"
        };
    }

    private static string FormatHints(ThemePalette p, params (string key, string action)[] hints)
    {
        return string.Join(" ", hints.Select(h =>
            $"{p.PrimaryText.AnsiFg}{h.key}{Reset}{p.SecondaryText.AnsiFg}:{h.action}{Reset}"));
    }
}
