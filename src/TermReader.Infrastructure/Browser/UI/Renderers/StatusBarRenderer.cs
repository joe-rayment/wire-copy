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

    public void RenderStatusBar(NavigationContext context, ViewMode mode, int terminalWidth = 0)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        _helpers.WriteLine();
        var width = terminalWidth > 0 ? terminalWidth : Console.WindowWidth;
        var separatorWidth = Math.Max(1, width - 1);
        _helpers.WriteLine($"{p.StatusBarSeparatorFg.AnsiFg}{new string('\u2500', separatorWidth)}{Reset}");

        var modeLabel = GetModeLabel(mode);
        var hints = GetKeyHints(mode, p);
        var search = !string.IsNullOrEmpty(context.SearchQuery) ? $" {p.SecondaryText.AnsiFg}|{Reset} {p.PromptFg.AnsiFg}/{context.SearchQuery}{Reset} {p.SecondaryText.AnsiFg}(n/N){Reset}" : string.Empty;
        var back = context.CanGoBack ? $"{p.SecondaryText.AnsiFg}[\u2190back]{Reset} " : string.Empty;
        var cacheBadge = context.IsFromCache ? $" {p.SecondaryText.AnsiFg}[cached {FormatCacheAge(context.CachedAt)}]{Reset}" : string.Empty;

        var statusMsg = !string.IsNullOrEmpty(context.StatusMessage)
            ? $" {p.SecondaryText.AnsiFg}|{Reset} {p.PromptFg.AnsiFg}{context.StatusMessage}{Reset}"
            : string.Empty;

        _helpers.WriteLine($"{back}{p.StatusBarTextFg.AnsiFg}{modeLabel}{Reset} {hints}{search}{cacheBadge}{statusMsg}");
    }

    private static string GetModeLabel(ViewMode mode)
    {
        return mode switch
        {
            ViewMode.Hierarchical => "LinkView",
            ViewMode.Readable => "ReaderView",
            ViewMode.CollectionList => "Reading List",
            ViewMode.CollectionItems => "Reading List",
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
                ("Enter", "open"),
                ("s", "save"),
                ("A", "save-all"),
                ("v", "reader"),
                ("?", "help")),
            ViewMode.Readable => FormatHints(
                p,
                ("+/-", "width"),
                ("v", "links"),
                ("b", "back"),
                ("?", "help")),
            ViewMode.CollectionList => FormatHints(
                p,
                ("Enter", "open"),
                ("?", "help")),
            ViewMode.CollectionItems => FormatHints(
                p,
                ("Enter", "open"),
                ("d", "remove"),
                ("J/K", "reorder"),
                ("b", "back"),
                ("?", "help")),
            ViewMode.Launcher => FormatHints(
                p,
                ("Enter", "open"),
                ("?", "help")),
            _ => $"{p.SecondaryText.AnsiFg}q:quit{Reset}"
        };
    }

    private static string FormatHints(ThemePalette p, params (string Key, string Action)[] hints)
    {
        return string.Join(" ", hints.Select(h =>
            $"{p.PrimaryText.AnsiFg}{h.Key}{Reset}{p.SecondaryText.AnsiFg}:{h.Action}{Reset}"));
    }

    private static string FormatCacheAge(DateTime? cachedAt)
    {
        if (cachedAt == null)
        {
            return "just now";
        }

        var age = DateTime.UtcNow - cachedAt.Value;
        return age.TotalMinutes switch
        {
            < 1 => "<1m ago",
            < 60 => $"{(int)age.TotalMinutes}m ago",
            _ => $"{(int)age.TotalHours}h ago"
        };
    }
}
