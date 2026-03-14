// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the unified two-line status bar for all views.
/// Line 1: mode label + adaptive key hints
/// Line 2: contextual vitals (URL/domain left, cache/status right)
/// Total budget: 3 lines (separator + line 1 + line 2).
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

    public void RenderStatusBar(
        NavigationContext context,
        ViewMode mode,
        int terminalWidth = 0,
        PreloadProgress? cacheProgress = null,
        double cacheUsagePercent = 0,
        int readerTotalLines = 0,
        int readerContentWidth = 0,
        int readerViewportHeight = 0)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = terminalWidth > 0 ? terminalWidth : Console.WindowWidth;
        var separatorWidth = Math.Max(1, width - 1);

        // Separator line
        _helpers.WriteLine($"{p.StatusBarSeparatorFg.AnsiFg}{new string('\u2500', separatorWidth)}{Reset}");

        // Line 1: mode label + adaptive key hints
        RenderLine1(context, mode, p, width);

        // Line 2: contextual vitals
        RenderLine2(context, mode, p, width, cacheProgress, cacheUsagePercent, readerTotalLines, readerContentWidth, readerViewportHeight);
    }

    internal static string GetModeLabel(ViewMode mode)
    {
        return mode switch
        {
            ViewMode.Hierarchical => "LinkView",
            ViewMode.Readable => "ReaderView",
            ViewMode.CollectionList => "Collections",
            ViewMode.CollectionItems => "Reading List",
            ViewMode.Launcher => "Launcher",
            _ => "Browser"
        };
    }

    internal static string GetAdaptiveHints(ViewMode mode, ThemePalette p, int availableWidth)
    {
        var tiers = GetHintTiers(mode);

        // Try each tier from largest to smallest
        foreach (var tier in tiers)
        {
            var formatted = FormatHints(p, tier);
            var displayWidth = RenderHelpers.GetDisplayWidth(formatted);
            if (displayWidth <= availableWidth)
            {
                return formatted;
            }
        }

        // Nothing fits
        return string.Empty;
    }

    internal static string FormatProgressBar(int cached, int total, ThemePalette p, bool isActive = false)
    {
        const int barLength = 10;
        const string ActiveColor = "\x1b[38;5;220m"; // Yellow/amber for active caching
        var filled = total > 0 ? (int)Math.Round((double)cached / total * barLength) : 0;
        var empty = barLength - filled;
        var bar = new string('\u25B0', filled) + new string('\u25B1', empty);
        var barColor = isActive ? ActiveColor : p.PromptFg.AnsiFg;
        var label = isActive ? "caching" : "cached";
        return $"{barColor}{bar}{Reset} {p.SecondaryText.AnsiFg}{cached}/{total} {label}{Reset}";
    }

    private static string FormatLine2Left(
        NavigationContext context,
        ViewMode mode,
        ThemePalette p,
        int readerTotalLines,
        int readerContentWidth,
        int readerViewportHeight)
    {
        // Reader mode: show line position, width, and progress
        if (mode == ViewMode.Readable && readerTotalLines > 0)
        {
            var progress = readerTotalLines > 0
                ? (int)((float)Math.Min(context.ScrollOffset + readerViewportHeight, readerTotalLines) / readerTotalLines * 100)
                : 100;

            var lineInfo = $"L{context.ScrollOffset + 1}/{readerTotalLines}";
            var widthInfo = $"W{readerContentWidth}";
            return $" {p.SecondaryText.AnsiFg}{lineInfo} {widthInfo} {progress}%{Reset}";
        }

        // Show URL/domain for page views
        var url = context.CurrentPage?.Url;
        if (!string.IsNullOrEmpty(url))
        {
            var domain = GetDomain(url);
            return $" {p.SecondaryText.AnsiFg}{domain}{Reset}";
        }

        return string.Empty;
    }

    private static string FormatLine2Right(
        NavigationContext context,
        ViewMode mode,
        ThemePalette p,
        PreloadProgress? cacheProgress,
        double cacheUsagePercent)
    {
        var parts = new List<string>();

        // Search info
        if (!string.IsNullOrEmpty(context.SearchQuery))
        {
            parts.Add($"{p.PromptFg.AnsiFg}/{context.SearchQuery}{Reset} {p.SecondaryText.AnsiFg}(n/N){Reset}");
        }

        // Cache progress mini-bar or cache badge
        var cachePart = FormatCacheIndicator(context, mode, p, cacheProgress);
        if (!string.IsNullOrEmpty(cachePart))
        {
            parts.Add(cachePart);
        }

        // Cache usage warning
        if (cacheUsagePercent >= 90)
        {
            parts.Add($"{p.PromptFg.AnsiFg}cache {cacheUsagePercent:F0}%{Reset}");
        }

        // Status message
        if (!string.IsNullOrEmpty(context.StatusMessage))
        {
            parts.Add($"{p.PromptFg.AnsiFg}{context.StatusMessage}{Reset}");
        }

        return parts.Count > 0 ? string.Join($" {p.SecondaryText.AnsiFg}\u2502{Reset} ", parts) + " " : string.Empty;
    }

    private static string FormatCacheIndicator(NavigationContext context, ViewMode mode, ThemePalette p, PreloadProgress? progress)
    {
        // Preload progress for hierarchical/collection views
        if ((mode == ViewMode.Hierarchical || mode == ViewMode.CollectionItems) && progress != null && progress.TotalCacheableLinks > 0)
        {
            if (progress.IsComplete)
            {
                return $"{p.SecondaryText.AnsiFg}all cached{Reset}";
            }

            return FormatProgressBar(progress.CachedCount, progress.TotalCacheableLinks, p, progress.IsActivelyFetching);
        }

        // Per-page cache badge for other views
        if (context.IsFromCache)
        {
            return $"{p.SecondaryText.AnsiFg}cached {RenderHelpers.FormatCacheAge(context.CachedAt)}{Reset}";
        }

        return string.Empty;
    }

    private static (string Key, string Action)[][] GetHintTiers(ViewMode mode)
    {
        return mode switch
        {
            ViewMode.Hierarchical =>
            [
                [("Enter", "open"), ("s", "save"), ("A", "save-all"), ("R", "refresh"), ("v", "reader"), ("?", "help")],
                [("Enter", "open"), ("s", "save"), ("R", "refresh"), ("v", "reader"), ("?", "help")],
                [("?", "help")],
            ],
            ViewMode.Readable =>
            [
                [("s", "save"), ("h/l", "width"), ("R", "refresh"), ("v", "links"), ("b", "back"), ("?", "help")],
                [("s", "save"), ("v", "links"), ("b", "back"), ("?", "help")],
                [("?", "help")],
            ],
            ViewMode.CollectionList =>
            [
                [("Enter", "open"), ("d", "delete"), ("b", "back"), ("?", "help")],
                [("Enter", "open"), ("b", "back"), ("?", "help")],
                [("?", "help")],
            ],
            ViewMode.CollectionItems =>
            [
                [("Enter", "open"), ("d", "remove"), ("X", "clear"), ("J/K", "reorder"), ("p", "podcast"), (":", "cmd"), ("b", "back"), ("?", "help")],
                [("Enter", "open"), ("d", "remove"), ("p", "podcast"), ("b", "back"), ("?", "help")],
                [("Enter", "open"), ("b", "back"), ("?", "help")],
                [("?", "help")],
            ],
            ViewMode.Launcher =>
            [
                [("Enter", "open"), ("a", "add"), ("d", "delete"), ("?", "help")],
                [("Enter", "open"), ("?", "help")],
                [("?", "help")],
            ],
            _ =>
            [
                [("q", "quit")],
            ],
        };
    }

    private static string FormatHints(ThemePalette p, (string Key, string Action)[] hints)
    {
        return string.Join(" ", hints.Select(h =>
            $"{p.PrimaryText.AnsiFg}{h.Key}{Reset}{p.SecondaryText.AnsiFg}:{h.Action}{Reset}"));
    }

    private static string GetDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return url.Length > 40 ? url[..40] : url;
    }

    private void RenderLine1(NavigationContext context, ViewMode mode, ThemePalette p, int width)
    {
        var back = context.CanGoBack ? $"{p.SecondaryText.AnsiFg}[\u2190]{Reset} " : string.Empty;
        var modeLabel = $"{p.StatusBarTextFg.AnsiFg}{GetModeLabel(mode)}{Reset}";

        // Calculate space used by back + mode label
        var backWidth = context.CanGoBack ? 5 : 0; // "[←] "
        var modeLabelWidth = GetModeLabel(mode).Length;
        var availableForHints = width - 1 - backWidth - modeLabelWidth - 1; // -1 for space after label

        var hints = GetAdaptiveHints(mode, p, availableForHints);

        var line1 = $"{back}{modeLabel} {hints}";
        if (RenderHelpers.GetDisplayWidth(line1) > width - 1)
        {
            line1 = RenderHelpers.TruncateText(line1, width - 1);
        }

        _helpers.WriteLine(line1);
    }

    private void RenderLine2(
        NavigationContext context,
        ViewMode mode,
        ThemePalette p,
        int width,
        PreloadProgress? cacheProgress,
        double cacheUsagePercent,
        int readerTotalLines,
        int readerContentWidth,
        int readerViewportHeight)
    {
        // Left side: contextual info
        var left = FormatLine2Left(context, mode, p, readerTotalLines, readerContentWidth, readerViewportHeight);

        // Right side: cache/search/status
        var right = FormatLine2Right(context, mode, p, cacheProgress, cacheUsagePercent);

        var leftWidth = RenderHelpers.GetDisplayWidth(left);
        var rightWidth = RenderHelpers.GetDisplayWidth(right);
        var maxWidth = width - 1;

        string line2;
        if (leftWidth + rightWidth + 1 <= maxWidth && rightWidth > 0)
        {
            var padding = maxWidth - leftWidth - rightWidth;
            line2 = $"{left}{new string(' ', Math.Max(1, padding))}{right}";
        }
        else if (rightWidth > 0 && leftWidth == 0)
        {
            line2 = right;
        }
        else
        {
            line2 = left;
            if (RenderHelpers.GetDisplayWidth(line2) > maxWidth)
            {
                line2 = RenderHelpers.TruncateText(line2, maxWidth);
            }
        }

        _helpers.WriteLine(line2);
    }
}
