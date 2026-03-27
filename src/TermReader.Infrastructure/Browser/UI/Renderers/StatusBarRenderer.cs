// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders a single-line status bar at the bottom of the screen.
/// Format: [←] MODE  leftContent     rightContent  ?:help
/// </summary>
internal class StatusBarRenderer
{
    private const string Reset = "\x1b[0m";
    private static readonly char[] SpinnerFrames = ['\u280B', '\u2819', '\u2839', '\u2838', '\u283C', '\u2834', '\u2826', '\u2827', '\u2807', '\u280F'];
    private static int _spinnerFrame;
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
        var maxWidth = width - 1;

        // Build components
        var back = context.CanGoBack ? $"{p.SecondaryText.AnsiFg}[\u2190]{Reset} " : string.Empty;
        var backWidth = context.CanGoBack ? 5 : 0;

        var modeBadge = FormatModeBadge(mode, p);
        var modeBadgeWidth = GetShortModeLabel(mode).Length + 2; // +2 for space padding

        var left = FormatLeftContent(context, mode, p, readerTotalLines, readerContentWidth, readerViewportHeight);
        var leftWidth = RenderHelpers.GetDisplayWidth(left);

        var right = FormatRightContent(context, mode, p, cacheProgress, cacheUsagePercent);
        var rightWidth = RenderHelpers.GetDisplayWidth(right);

        // Responsive help hint: full at wide, abbreviated at narrow
        string helpHint;
        int helpWidth;
        if (width >= 60)
        {
            helpHint = $" {p.SecondaryText.AnsiFg}?:help{Reset}";
            helpWidth = 7;
        }
        else
        {
            helpHint = $" {p.SecondaryText.AnsiFg}?{Reset}";
            helpWidth = 2;
        }

        // Layout: [back][mode] [left]    [right] [?:help]
        var fixedWidth = backWidth + modeBadgeWidth + 1 + helpWidth; // +1 space after mode
        var contentWidth = maxWidth - fixedWidth;

        // At narrow widths (<60), drop right-side badges entirely
        if (width < 60 && rightWidth > contentWidth - 5)
        {
            right = string.Empty;
            rightWidth = 0;
        }

        // If right + left don't fit, truncate left first
        if (leftWidth + rightWidth > contentWidth)
        {
            var maxLeft = contentWidth - rightWidth;
            if (maxLeft > 3)
            {
                left = RenderHelpers.TruncateText(left, maxLeft);
                leftWidth = maxLeft;
            }
            else
            {
                left = string.Empty;
                leftWidth = 0;
            }
        }

        var padding = Math.Max(1, contentWidth - leftWidth - rightWidth);
        var line = $"{back}{modeBadge} {left}{new string(' ', padding)}{right}{helpHint}";

        if (RenderHelpers.GetDisplayWidth(line) > maxWidth)
        {
            line = RenderHelpers.TruncateText(line, maxWidth);
        }

        _helpers.WriteLine(line);
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

        foreach (var tier in tiers)
        {
            var formatted = FormatHints(p, tier);
            var displayWidth = RenderHelpers.GetDisplayWidth(formatted);
            if (displayWidth <= availableWidth)
            {
                return formatted;
            }
        }

        return string.Empty;
    }

    internal static string FormatProgressBar(int cached, int total, ThemePalette p, bool isActive = false, string? currentUrl = null)
    {
        const int barLength = 6;
        var fraction = total > 0 ? (double)cached / total : 0.0;

        if (isActive)
        {
            var spinner = SpinnerFrames[_spinnerFrame % SpinnerFrames.Length];
            _spinnerFrame++;
            var bar = Components.Indicators.RenderEighthBlockBar(p.PromptFg.AnsiFg, p.SecondaryText.AnsiFg, fraction, barLength);
            return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset} {bar} {p.PromptFg.AnsiFg}{spinner}{Reset}";
        }

        return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset}";
    }

    private static string FormatModeBadge(ViewMode mode, ThemePalette p)
    {
        var label = GetShortModeLabel(mode);
        return $"{p.StatusBarTextFg.AnsiFg}{label}{Reset}";
    }

    private static string GetShortModeLabel(ViewMode mode)
    {
        return mode switch
        {
            ViewMode.Hierarchical => "LINK",
            ViewMode.Readable => "READ",
            ViewMode.CollectionList => "LIST",
            ViewMode.CollectionItems => "SAVE",
            ViewMode.Launcher => "HOME",
            _ => "VIEW"
        };
    }

    private static string FormatLeftContent(
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
            return $"{p.SecondaryText.AnsiFg}{lineInfo} {widthInfo} {progress}%{Reset}";
        }

        // Collection items: show collection name
        if (mode == ViewMode.CollectionItems)
        {
            var name = context.CurrentPage?.Metadata?.Title;
            if (!string.IsNullOrEmpty(name))
            {
                return $"{p.SecondaryText.AnsiFg}{name}{Reset}";
            }
        }

        // Show URL/domain for page views
        var url = context.CurrentPage?.Url;
        if (!string.IsNullOrEmpty(url))
        {
            var domain = GetDomain(url);
            return $"{p.SecondaryText.AnsiFg}{domain}{Reset}";
        }

        return string.Empty;
    }

    private static string FormatRightContent(
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

        // Page classification badge
        if (context.CurrentPage?.Classification == PageClassification.LinkList)
        {
            parts.Add($"{p.SecondaryText.AnsiFg}index{Reset}");
        }

        // AI hierarchy badge
        if (context.IsAiHierarchy && (mode == ViewMode.Hierarchical || mode == ViewMode.Readable))
        {
            parts.Add($"{p.SecondaryText.AnsiFg}AI{Reset}");
        }

        // Cache progress or badge
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

        return parts.Count > 0 ? string.Join($" {p.SecondaryText.AnsiFg}\u00b7{Reset} ", parts) : string.Empty;
    }

    private static string FormatCacheIndicator(NavigationContext context, ViewMode mode, ThemePalette p, PreloadProgress? progress)
    {
        if ((mode == ViewMode.Hierarchical || mode == ViewMode.CollectionItems) && progress != null)
        {
            if (progress.TotalCacheableLinks > 0)
            {
                if (progress.IsComplete)
                {
                    return $"{p.SecondaryText.AnsiFg}\u2713 cached{Reset}";
                }

                return FormatProgressBar(progress.CachedCount, progress.TotalCacheableLinks, p, progress.IsActivelyFetching, progress.CurrentlyFetchingUrl);
            }

            if (progress.PaywalledLinkCount > 0)
            {
                if (context.CurrentPage?.HasReadableContent() == true ||
                    context.CurrentPage?.LinkTree?.TotalLinks > 0)
                {
                    return $"{p.SecondaryText.AnsiFg}paywall{Reset}";
                }

                return $"{p.SecondaryText.AnsiFg}paywall \u00b7 {Reset}{p.PrimaryText.AnsiFg}I{Reset}{p.SecondaryText.AnsiFg}:login{Reset}";
            }
        }

        if (context.IsFromCache)
        {
            return $"{p.SecondaryText.AnsiFg}{RenderHelpers.FormatCacheAge(context.CachedAt)}{Reset}";
        }

        return string.Empty;
    }

    private static (string Key, string Action)[][] GetHintTiers(ViewMode mode)
    {
        return mode switch
        {
            ViewMode.Hierarchical =>
            [
                [("Enter", "open"), ("Space", "select"), ("s", "save"), ("A", "save-all"), ("Shift+R", "refresh"), ("v", "reader"), ("?", "help")],
                [("Enter", "open"), ("Space", "select"), ("s", "save"), ("Shift+R", "refresh"), ("v", "reader"), ("?", "help")],
                [("s", "save"), ("?", "help")],
            ],
            ViewMode.Readable =>
            [
                [("s", "save"), ("o", "browser"), ("[]", "width"), ("Shift+R", "refresh"), ("v", "links"), ("b", "back"), ("?", "help")],
                [("s", "save"), ("o", "browser"), ("v", "links"), ("b", "back"), ("?", "help")],
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
                [("Enter", "open"), ("o", "go to url"), ("a", "add"), ("d", "delete"), ("?", "help")],
                [("Enter", "open"), ("o", "go to url"), ("?", "help")],
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
            $"{p.GetAccentFg().AnsiFg}{h.Key}{Reset}{p.SecondaryText.AnsiFg}:{h.Action}{Reset}"));
    }

    private static string GetDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return url.Length > 40 ? url[..40] : url;
    }
}
