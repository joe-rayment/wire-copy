// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders a 2-line status bar at the bottom of the screen.
/// Line 1: full-width dimmed separator rule (──────)
/// Line 2: [←] MODE  leftContent     rightContent  ?:help
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
        int readerViewportHeight = 0,
        string? layoutVariantLabel = null,
        IReadOnlyList<string>? missingCookieDomains = null,
        HumanActionRequired? requiredAction = null)
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

        var right = FormatRightContent(context, mode, p, cacheProgress, cacheUsagePercent, layoutVariantLabel, missingCookieDomains, requiredAction);
        var rightWidth = RenderHelpers.GetDisplayWidth(right);

        // Responsive help hint: show preview controls or standard help
        string helpHint;
        int helpWidth;
        if (context.IsInPreviewMode)
        {
            helpHint = $" {p.GetAccentFg().AnsiFg}\u25c0/\u25b6{Reset}{p.GetDimFg().AnsiFg}:cycle{Reset} {p.GetAccentFg().AnsiFg}Enter{Reset}{p.GetDimFg().AnsiFg}:save{Reset} {p.GetAccentFg().AnsiFg}Esc{Reset}{p.GetDimFg().AnsiFg}:cancel{Reset}";
            helpWidth = 30;
        }
        else if (width >= 60)
        {
            helpHint = $" {p.GetAccentFg().AnsiFg}?{Reset}{p.GetDimFg().AnsiFg}:help{Reset}";
            helpWidth = 7;
        }
        else
        {
            helpHint = $" {p.GetAccentFg().AnsiFg}?{Reset}";
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

        // Line 1: full-width dimmed separator
        _helpers.WriteLine(Components.Borders.DimmedRule(p, width));

        // Line 2: status bar content
        _helpers.WriteLine(line);
    }

    internal static string GetModeLabel(ViewMode mode)
    {
        // NOTE: ViewMode.Launcher is intentionally retained here even though
        // the StatusBar is not rendered for the launcher (workspace-m8x2).
        // This helper is also called by KeybindingPopup.Render to title the
        // help popup, which the launcher DOES open. The status-bar-internal
        // helpers (GetShortModeLabel, GetHintTiers) drop the launcher arm
        // because they are only reached from RenderStatusBar / GetAdaptiveHints.
        return mode switch
        {
            ViewMode.Hierarchical => "LinkView",
            ViewMode.Readable => "ReaderView",
            ViewMode.CollectionList => "Collections",
            ViewMode.CollectionItems => "ReadingList",
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
        var isComplete = cached >= total && total > 0;

        if (isActive)
        {
            var spinner = SpinnerFrames[_spinnerFrame % SpinnerFrames.Length];
            _spinnerFrame++;
            var bar = Components.Indicators.RenderEighthBlockBar(
                p.GetWarningFg().AnsiFg, p.GetMutedFg().AnsiFg, (double)cached / Math.Max(1, total), barLength);
            return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset} {bar} {p.GetWarningFg().AnsiFg}{spinner}{Reset}";
        }

        if (isComplete)
        {
            var bar = Components.Indicators.RenderEighthBlockBar(
                p.GetSuccessFg().AnsiFg, p.GetMutedFg().AnsiFg, 1.0, barLength);
            return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset} {bar}";
        }

        return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset}";
    }

    /// <summary>
    /// Plays a bright-green wave animation across the dimmed separator rule (line 1 of the status bar).
    /// 10 frames at 60ms = 600ms total. A segment of 4 characters sweeps left to right.
    /// Must be called from a background thread. Uses ConsoleSync.Lock for thread safety.
    /// </summary>
    internal static void PlayCacheWarmWave(ThemePalette p, int width)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        const int frameCount = 10;
        const int segmentWidth = 4;
        const int frameDelayMs = 60;
        var ruleWidth = Math.Max(1, width);
        var dimFg = p.GetDimFg().AnsiFg;
        var brightFg = p.PrimaryText.AnsiFg;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var wavePos = (int)(frame * (ruleWidth / (double)frameCount));
            var beforeLen = Math.Max(0, wavePos);
            var brightLen = Math.Min(segmentWidth, ruleWidth - wavePos);
            var afterLen = Math.Max(0, ruleWidth - wavePos - segmentWidth);

            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(0, Console.WindowHeight - 2);
                    Console.Write(
                        $"{dimFg}\x1b[2m{new string('\u2500', beforeLen)}{Reset}" +
                        $"{brightFg}{new string('\u2500', brightLen)}{Reset}" +
                        $"{dimFg}\x1b[2m{new string('\u2500', afterLen)}{Reset}");
                }
                catch
                {
                    // Ignore console errors (e.g., redirected output)
                }
            }

            Thread.Sleep(frameDelayMs);
        }

        // Restore the separator to its normal dimmed state
        lock (ConsoleSync.Lock)
        {
            try
            {
                Console.SetCursorPosition(0, Console.WindowHeight - 2);
                Console.Write($"{dimFg}\x1b[2m{new string('\u2500', ruleWidth)}{Reset}");
            }
            catch
            {
                // Ignore console errors
            }
        }
    }

    /// <summary>
    /// Plays a 3-frame color pulse on the cache progress count text.
    /// Cycles: AccentFg (cyan) -> PrimaryText (green) -> SecondaryText (settled).
    /// 3 frames at 100ms = 300ms total. Must be called from a background thread.
    /// </summary>
    internal static void PlayCacheItemPulse(ThemePalette p, int count, int total, int col, int row)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        const int frameDelayMs = 100;
        var colors = new[] { p.GetAccentFg().AnsiFg, p.PrimaryText.AnsiFg, p.SecondaryText.AnsiFg };
        var text = $"{count}/{total}";

        foreach (var color in colors)
        {
            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(col, row);
                    Console.Write($"{color}{text}{Reset}");
                }
                catch
                {
                    // Ignore console errors
                }
            }

            Thread.Sleep(frameDelayMs);
        }
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
            ViewMode.Hierarchical => "LinkView",
            ViewMode.Readable => "ReaderView",
            ViewMode.CollectionList => "Collections",
            ViewMode.CollectionItems => "ReadingList",
            ViewMode.Launcher => throw new InvalidOperationException("StatusBar is not rendered for the launcher"),
            _ => "Browser"
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
        double cacheUsagePercent,
        string? layoutVariantLabel = null,
        IReadOnlyList<string>? missingCookieDomains = null,
        HumanActionRequired? requiredAction = null)
    {
        var parts = new List<string>();

        // Typed human-action badge takes precedence over the legacy cookie badge
        // (workspace-0b9s). Format: "⏸ {verb} at {domain} · Shift+O:open" — replaces
        // the confusing "🍪✗ nytimes.com Shift+I:login" copy that read as "something
        // about cookies" when the actual block was a CAPTCHA / login wall / consent
        // banner / etc.
        if (requiredAction != null)
        {
            var verb = GetActionVerb(requiredAction.Variant);
            var domainText = string.IsNullOrWhiteSpace(requiredAction.Domain) ? "site" : requiredAction.Domain;
            parts.Add(
                $"{p.GetWarningFg().AnsiFg}⏸{Reset} " +
                $"{p.SecondaryText.AnsiFg}{verb} at {domainText}{Reset} " +
                $"{p.SecondaryText.AnsiFg}·{Reset} " +
                $"{p.GetAccentFg().AnsiFg}Shift+O{Reset}{p.GetDimFg().AnsiFg}:open{Reset}");
        }
        else if (missingCookieDomains is { Count: > 0 })
        {
            // Legacy cookie badge — kept for backwards compat with consumers that haven't
            // wired the typed RequiredAction signal yet.
            var domainList = string.Join(",", missingCookieDomains);
            parts.Add(
                $"{p.PromptFg.AnsiFg}\U0001F36A✗{Reset} " +
                $"{p.SecondaryText.AnsiFg}{domainList}{Reset} " +
                $"{p.GetAccentFg().AnsiFg}Shift+I{Reset}{p.GetDimFg().AnsiFg}:login{Reset}");
        }

        // Search info
        if (!string.IsNullOrEmpty(context.SearchQuery))
        {
            parts.Add($"{p.PromptFg.AnsiFg}/{context.SearchQuery}{Reset} {p.SecondaryText.AnsiFg}(n/N){Reset}");
        }

        // Layout preview indicator (takes priority over other badges)
        if (context.IsInPreviewMode && context.PreviewLabel != null)
        {
            parts.Add($"{p.PromptFg.AnsiFg}{context.PreviewLabel}{Reset}");
        }
        else
        {
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

            // Layout chooser hint — always visible on link list pages in hierarchical view
            if (mode == ViewMode.Hierarchical &&
                context.CurrentPage?.Classification == PageClassification.LinkList)
            {
                parts.Add($"{p.GetAccentFg().AnsiFg}Ctrl+L{Reset}{p.GetDimFg().AnsiFg}:layout{Reset}");
            }

            // Layout variant indicator (e.g., "Grid 1/3")
            if (!string.IsNullOrEmpty(layoutVariantLabel))
            {
                parts.Add($"{p.SecondaryText.AnsiFg}{layoutVariantLabel}{Reset}");
            }
        }

        // Cache progress or badge
        var cachePart = FormatCacheIndicator(context, mode, p, cacheProgress);
        if (!string.IsNullOrEmpty(cachePart))
        {
            parts.Add(cachePart);
        }

        // Selection count badge (persistent, replaces transient status message)
        var selCount = context.CurrentPage?.LinkTree?.SelectionCount ?? 0;
        if (selCount > 0 && mode == ViewMode.Hierarchical)
        {
            parts.Add($"{p.PromptFg.AnsiFg}{selCount} sel{Reset}");
        }

        // Cache usage warning
        if (cacheUsagePercent >= 90)
        {
            parts.Add($"{p.PromptFg.AnsiFg}cache {cacheUsagePercent:F0}%{Reset}");
        }

        // Speed reading WPM indicator
        if (context.IsSpeedReadActive)
        {
            parts.Add($"{p.PromptFg.AnsiFg}\u25b6 {context.SpeedReadWpm} WPM{Reset}");
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
                if (progress.IsComplete && progress.CachedCount > 0)
                {
                    if (progress.NeedsBrowserCount > 0)
                    {
                        return $"{p.SecondaryText.AnsiFg}{progress.CachedCount}/{progress.TotalCacheableLinks} \u2713{Reset}";
                    }

                    return $"{p.SecondaryText.AnsiFg}\u2713 cached{Reset}";
                }

                if (progress.IsActivelyFetching)
                {
                    return FormatProgressBar(progress.CachedCount, progress.TotalCacheableLinks, p, true, progress.CurrentlyFetchingUrl);
                }

                // Stalled: not actively fetching but not complete either
                // (or "complete" with nothing cached — all needsJs)
                var count = $"{progress.CachedCount}/{progress.TotalCacheableLinks}";
                if (progress.NeedsBrowserCount > 0)
                {
                    return $"{p.SecondaryText.AnsiFg}{count} \u00b7 {Reset}{p.GetAccentFg().AnsiFg}I{Reset}{p.SecondaryText.AnsiFg}:login{Reset}";
                }

                return $"{p.SecondaryText.AnsiFg}{count} \u00b7 paused{Reset}";
            }

            if (progress.PaywalledLinkCount > 0)
            {
                if (context.CurrentPage?.HasReadableContent() == true ||
                    context.CurrentPage?.LinkTree?.TotalLinks > 0)
                {
                    return $"{p.SecondaryText.AnsiFg}paywall{Reset}";
                }

                return $"{p.SecondaryText.AnsiFg}paywall \u00b7 {Reset}{p.GetAccentFg().AnsiFg}I{Reset}{p.SecondaryText.AnsiFg}:login{Reset}";
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
                [("s", "save"), ("f", "speed-read"), ("o", "browser"), ("[]", "width"), ("Shift+R", "refresh"), ("v", "links"), ("b", "back"), ("?", "help")],
                [("s", "save"), ("f", "speed-read"), ("o", "browser"), ("v", "links"), ("b", "back"), ("?", "help")],
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
            ViewMode.Launcher => throw new InvalidOperationException("StatusBar is not rendered for the launcher"),
            _ =>
            [
                [("q", "quit")],
            ],
        };
    }

    private static string FormatHints(ThemePalette p, (string Key, string Action)[] hints)
    {
        return string.Join(" ", hints.Select(h =>
            $"{p.GetAccentFg().AnsiFg}{h.Key}{Reset}{p.GetDimFg().AnsiFg}:{h.Action}{Reset}"));
    }

    private static string GetActionVerb(HumanActionVariant variant)
    {
        // Verb chosen to read naturally inside "{verb} at {domain}" so the badge
        // works as a single sentence in the status bar (workspace-0b9s).
        return variant switch
        {
            HumanActionVariant.Captcha => "captcha",
            HumanActionVariant.Login => "login",
            HumanActionVariant.CookieConsent => "consent",
            HumanActionVariant.TwoFactor => "2FA",
            HumanActionVariant.Paywall => "paywall",
            HumanActionVariant.RegionBlock => "region-block",
            _ => "action needed",
        };
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
