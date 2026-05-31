// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.UI;

/// <summary>
/// Renders pages to the terminal.
/// Thin dispatcher that delegates to view-specific sub-renderers.
/// </summary>
public class TerminalPageRenderer : IPageRenderer
{
    private const string Reset = "\x1b[0m";
    private const int MinBoxWidth = 30;

    // Maximum inner width (content + 2 padding spaces) of the centered status box.
    // Practical content cap = MaxBoxContentWidth - 2 = 54 chars per line. Sized to
    // accommodate longest plausible variant copy with a 22-char domain (e.g.
    // "subdomain.nytimes.com") while still fitting an 80-col terminal:
    //   boxWidth = 56 + 4 = 60, leftPad = (80 - 60) / 2 = 10. Bumped from 46 in
    //   workspace-0b9s after QA flagged TwoFactor and Login overflow at 80 cols.
    private const int MaxBoxContentWidth = 56;
    private static readonly char[] SpinnerFrames = ['\u280B', '\u2819', '\u2839', '\u2838', '\u283C', '\u2834', '\u2826', '\u2827', '\u2807', '\u280F'];

    private readonly IThemeProvider _themeProvider;
    private readonly RenderHelpers _helpers;
    private readonly LinkTreeRenderer _linkTreeRenderer;
    private readonly ArticleRenderer _articleRenderer;
    private readonly CollectionRenderer _collectionRenderer;
    private readonly LauncherRenderer _launcherRenderer;
    private readonly StatusBarRenderer _statusBarRenderer;
    private readonly PreloadDetailRenderer _preloadDetailRenderer;
    private IReadOnlyList<LineCacheManager.ParagraphSpan>? _paragraphSpans;

    public TerminalPageRenderer(IThemeProvider themeProvider, ILogger<TerminalPageRenderer> logger)
        : this(themeProvider, logger, podcastJobManager: null)
    {
    }

    // workspace-vkhr Phase D: podcast job manager is optional so existing
    // tests that construct the renderer without DI still compile. The DI
    // registration in BrowserDependencyInjection ALWAYS provides one, so
    // the production path renders the detached-job badge correctly.
    public TerminalPageRenderer(
        IThemeProvider themeProvider,
        ILogger<TerminalPageRenderer> logger,
        IPodcastBackgroundJobManager? podcastJobManager)
    {
        _themeProvider = themeProvider;
        _helpers = new RenderHelpers();
        _linkTreeRenderer = new LinkTreeRenderer(_helpers, themeProvider);
        _articleRenderer = new ArticleRenderer(_helpers, themeProvider);
        _collectionRenderer = new CollectionRenderer(_helpers, themeProvider);
        _launcherRenderer = new LauncherRenderer(_helpers, themeProvider);
        _statusBarRenderer = new StatusBarRenderer(_helpers, themeProvider, podcastJobManager);
        _preloadDetailRenderer = new PreloadDetailRenderer(_helpers, themeProvider);
    }

    public void RenderHierarchical(Page page, NavigationContext context, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();

        var linkCount = page.LinkTree?.TotalLinks ?? 0;
        var sectionCount = page.LinkTree?.Root.Children.Count(c => c.IsGroupHeader) ?? 0;
        _linkTreeRenderer.RenderHeader(page.Metadata, page.Url, options, linkCount, sectionCount);

        // Status bar is 2 lines (separator + content row) at the bottom; reserve both.
        var remainingHeight = Math.Max(3, options.TerminalHeight - _helpers.LinesWritten - 2);

        if (page.LinkTree != null)
        {
            _linkTreeRenderer.RenderLinkTree(page.LinkTree, context, remainingHeight, options);
        }
        else
        {
            var ep = BuiltInThemes.Get(_themeProvider.CurrentTheme);
            _helpers.WriteLine();
            _helpers.WriteLine($"  {ep.SecondaryText.AnsiFg}This page is keeping its links to itself.{Reset}");
            if (page.ReadableContent != null)
            {
                _helpers.WriteLine($"  {ep.SecondaryText.AnsiFg}Try reader view \u2014 press{Reset} {ep.GetAccentFg().AnsiFg}v{Reset}");
            }

            _helpers.WriteLine();
        }

        _helpers.PositionAtBottom();
        _statusBarRenderer.RenderStatusBar(context, ViewMode.Hierarchical, options.TerminalWidth, options.CacheProgress, options.CacheUsagePercent, layoutVariantLabel: options.LayoutVariantLabel, missingCookieDomains: options.MissingCookieDomains, requiredAction: options.RequiredAction);
        RenderPreloadDetailOverlay(options);
        RenderToastOverlay(context, options.TerminalWidth);
    }

    public void RenderReadable(Page page, NavigationContext context, RenderOptions options, List<string>? wrappedLines = null)
    {
        var paragraphSpans = _paragraphSpans;
        _paragraphSpans = null; // Consume — set fresh each render cycle
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();

        // Title bar matching LinkView format: "Title                              domain"
        var titleText = page.ReadableContent?.Title ?? page.Metadata?.Title ?? "Untitled";
        _linkTreeRenderer.RenderHeader(
            page.Metadata ?? new Domain.ValueObjects.Browser.PageMetadata { Title = titleText },
            page.Url,
            options);

        if (page.ReadableContent == null)
        {
            _helpers.WriteLine();
            if (page.Classification == Domain.Enums.Browser.PageClassification.LinkList)
            {
                _helpers.WriteLine("  This is a link directory. Press 'v' to browse links.");
            }
            else
            {
                RenderExtractionFailureBox(options.TerminalWidth);
            }

            _helpers.PositionAtBottom();
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth, layoutVariantLabel: options.LayoutVariantLabel, missingCookieDomains: options.MissingCookieDomains, requiredAction: options.RequiredAction);
            RenderPreloadDetailOverlay(options);
            RenderToastOverlay(context, options.TerminalWidth);
            return;
        }

        // workspace-umi7: viewport height MUST agree with BrowserOrchestrator's
        // GetReaderViewportHeight so the speed-read scroller doesn't think the
        // visible area is taller than what we actually paint. Both routes go
        // through ReaderLayout.ComputeContentHeight. An invariant test verifies
        // that LinkTreeRenderer.RenderHeader writes exactly ReaderLayout.HeaderLines
        // lines so this stays correct as the codebase evolves.
        var viewportHeight = ReaderLayout.ComputeContentHeight(options.TerminalHeight);
        var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        // Center article content when narrower than terminal
        _helpers.LeftMargin = Math.Max(0, (options.TerminalWidth - options.MaxContentWidth) / 2);

        if (wrappedLines != null)
        {
            _articleRenderer.RenderLineBasedContent(wrappedLines, context, viewportHeight, options, paragraphSpans);
            _helpers.LeftMargin = 0;
            _helpers.RenderEndOfContentRule(palette, options.TerminalWidth);
            _helpers.PositionAtBottom();
            _statusBarRenderer.RenderStatusBar(
                context,
                ViewMode.Readable,
                options.TerminalWidth,
                readerTotalLines: wrappedLines.Count,
                readerContentWidth: options.MaxContentWidth,
                readerViewportHeight: viewportHeight,
                layoutVariantLabel: options.LayoutVariantLabel,
                missingCookieDomains: options.MissingCookieDomains,
                requiredAction: options.RequiredAction);
        }
        else
        {
            _articleRenderer.RenderArticleContent(page.ReadableContent, context, viewportHeight, options);
            _helpers.LeftMargin = 0;
            _helpers.RenderEndOfContentRule(palette, options.TerminalWidth);
            _helpers.PositionAtBottom();
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth, layoutVariantLabel: options.LayoutVariantLabel, missingCookieDomains: options.MissingCookieDomains, requiredAction: options.RequiredAction);
        }

        RenderPreloadDetailOverlay(options);
        RenderToastOverlay(context, options.TerminalWidth);
    }

    public void RenderLoading(string url, string? status = null)
    {
        RenderLoading(url, status, elapsedMs: 0);
    }

    public void RenderLoading(string url, string? status, long elapsedMs)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var label = status ?? "Loading...";

        // Animated spinner: cycles through 10 braille frames every 500ms
        var frameIndex = (int)((elapsedMs / 500) % SpinnerFrames.Length);
        var spinner = SpinnerFrames[frameIndex];

        // Elapsed seconds — visible proof the app is running
        var elapsed = elapsedMs >= 1000 ? $" {elapsedMs / 1000}s" : string.Empty;

        var truncatedUrl = RenderHelpers.TruncateUrl(url, MaxBoxContentWidth - 2);

        var lines = new List<CenteredBoxLine>
        {
            CenteredBoxLine.Empty,
            new($"{p.PromptFg.AnsiFg}{spinner}{Reset} {p.PrimaryText.AnsiFg}{label}{Reset}{(elapsed.Length > 0 ? $"{p.SecondaryText.AnsiFg}{elapsed}{Reset}" : string.Empty)}", $"{spinner} {label}{elapsed}"),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}{truncatedUrl}{Reset}", truncatedUrl),
            CenteredBoxLine.Empty,
            new($"{p.GetAccentFg().AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:cancel{Reset}", "Esc:cancel"),
            CenteredBoxLine.Empty,
        };

        RenderCenteredBox(lines, p.GetMutedFg());
    }

    public void RenderError(string message, string url)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var truncatedUrl = RenderHelpers.TruncateUrl(url, MaxBoxContentWidth - 2);
        var truncatedMsg = RenderHelpers.TruncateText(message, MaxBoxContentWidth - 2);

        var lines = new List<CenteredBoxLine>
        {
            CenteredBoxLine.Empty,
            new($"{p.PrimaryText.AnsiFg}Something went wrong{Reset}", "Something went wrong"),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}{truncatedMsg}{Reset}", truncatedMsg),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}{truncatedUrl}{Reset}", truncatedUrl),
            CenteredBoxLine.Empty,
            new(
                $"{p.GetAccentFg().AnsiFg}b{Reset}{p.SecondaryText.AnsiFg}:back{Reset}  {p.GetAccentFg().AnsiFg}Shift+R{Reset}{p.SecondaryText.AnsiFg}:retry{Reset}",
                "b:back  Shift+R:retry"),
            CenteredBoxLine.Empty,
        };

        RenderCenteredBox(lines, p.ErrorFg);
    }

    public void RenderChallenge(string url)
    {
        // Legacy entry-point preserved for any caller still wired to the old API
        // (workspace-0b9s leaves this in place per "thin wrapper" rule). New code
        // should prefer RenderHumanAction with a typed HumanActionRequired signal.
        var domain = ExtractDomainSafe(url);
        RenderHumanAction(
            new HumanActionRequired(Domain.Enums.Browser.HumanActionVariant.Captcha, domain),
            url);
    }

    public void RenderHumanAction(HumanActionRequired action, string url)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var domain = !string.IsNullOrWhiteSpace(action.Domain) ? action.Domain : ExtractDomainSafe(url);
        var truncatedUrl = RenderHelpers.TruncateUrl(url, MaxBoxContentWidth - 2);

        var copy = GetHumanActionCopy(action.Variant, domain);
        var (headlinePlain, bodyPlain, hintPlain) = copy;

        var headlineStyled = $"{p.GetWarningFg().AnsiFg}\u2847{Reset} {p.PrimaryText.AnsiFg}{headlinePlain}{Reset}";
        var headlineFull = $"\u2847 {headlinePlain}";

        var bodyStyled = $"{p.SecondaryText.AnsiFg}{bodyPlain}{Reset}";

        // Hint line: highlight the key verbs (Shift+O / Shift+R / Shift+I / b)
        var hintStyled = StyleHintLine(hintPlain, p);

        var lines = new List<CenteredBoxLine>
        {
            CenteredBoxLine.Empty,
            new(headlineStyled, headlineFull),
            CenteredBoxLine.Empty,
            new(bodyStyled, bodyPlain),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}{truncatedUrl}{Reset}", truncatedUrl),
            CenteredBoxLine.Empty,
            new(hintStyled, hintPlain),
            CenteredBoxLine.Empty,
        };

        RenderCenteredBox(lines, p.GetWarningFg());
    }

    /// <summary>
    /// Public so tests can verify the variant copy without going through the
    /// rendering pipeline. Returns the (headline, body, hint) plain-text triple
    /// that <see cref="RenderHumanAction"/> renders inside the warning-bordered box.
    /// </summary>
#pragma warning disable SA1204 // Static members should appear before non-static members — kept adjacent to RenderHumanAction for readability.
    public static (string Headline, string Body, string Hint) GetHumanActionCopy(
        Domain.Enums.Browser.HumanActionVariant variant,
        string domain)
    {
        var d = string.IsNullOrWhiteSpace(domain) ? "this site" : domain;

        return variant switch
        {
            Domain.Enums.Browser.HumanActionVariant.Captcha => (
                "Site is showing a CAPTCHA",
                $"Solve it in the browser window, then press R",
                $"{d} \u2014 Shift+O:open  b:back"),
            Domain.Enums.Browser.HumanActionVariant.Login => (
                $"Log in at {d} in your browser",
                "Then press R to refresh.",
                "Tip: Shift+I imports cookies after login"),
            Domain.Enums.Browser.HumanActionVariant.CookieConsent => (
                "Cookie consent banner blocking content",
                "Accept it in the browser, then press R",
                $"{d} \u2014 Shift+O:open  b:back"),
            Domain.Enums.Browser.HumanActionVariant.TwoFactor => (
                $"Two-factor code required at {d}",
                "Complete verification in the browser, then press R",
                "Shift+O:open  b:back"),
            Domain.Enums.Browser.HumanActionVariant.Paywall => (
                $"Article is paywalled at {d}",
                "Log in or open in browser",
                "Shift+O:open  b:back"),
            Domain.Enums.Browser.HumanActionVariant.RegionBlock => (
                "Site blocks this region (HTTP 451)",
                "Try a different network or VPN",
                "b:back"),
            _ => (
                $"Action needed at {d}",
                "Open it in your browser, then press R",
                "Shift+O:open  b:back"),
        };
    }
#pragma warning restore SA1204

    public void RenderInteractiveRefresh(string url)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var truncatedUrl = RenderHelpers.TruncateUrl(url, MaxBoxContentWidth - 2);

        var lines = new List<CenteredBoxLine>
        {
            CenteredBoxLine.Empty,
            new($"{p.PrimaryText.AnsiFg}Interactive refresh{Reset}", "Interactive refresh"),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}{truncatedUrl}{Reset}", truncatedUrl),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}Complete any captcha or login in the browser.{Reset}", "Complete any captcha or login in the browser."),
            new(
                $"{p.GetAccentFg().AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:accept{Reset}  {p.GetAccentFg().AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:cancel{Reset}",
                "Enter:accept  Esc:cancel"),
            CenteredBoxLine.Empty,
        };

        RenderCenteredBox(lines, p.GetMutedFg());
    }

    public void RenderManualLogin(string url, string domain)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var truncatedUrl = RenderHelpers.TruncateUrl(url, MaxBoxContentWidth - 2);
        var loginMsg = RenderHelpers.TruncateText($"Login required for {domain}", MaxBoxContentWidth - 2);

        var lines = new List<CenteredBoxLine>
        {
            CenteredBoxLine.Empty,
            new($"{p.PrimaryText.AnsiFg}{loginMsg}{Reset}", loginMsg),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}{truncatedUrl}{Reset}", truncatedUrl),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}Waiting for login to complete...{Reset}", "Waiting for login to complete..."),
            CenteredBoxLine.Empty,
        };

        RenderCenteredBox(lines, p.GetMutedFg());
    }

    public void RenderCollectionList(List<Collection> collections, int selectedIndex, Guid? defaultCollectionId, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _collectionRenderer.RenderCollectionList(collections, selectedIndex, defaultCollectionId, scrollOffset, options);
        _helpers.PositionAtBottom();
        _statusBarRenderer.RenderStatusBar(new NavigationContext { ViewMode = ViewMode.CollectionList }, ViewMode.CollectionList, options.TerminalWidth, layoutVariantLabel: options.LayoutVariantLabel);
    }

    public void RenderCollectionItems(Collection collection, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _collectionRenderer.RenderCollectionItems(collection, selectedIndex, scrollOffset, options);
        _helpers.PositionAtBottom();
        _statusBarRenderer.RenderStatusBar(new NavigationContext { ViewMode = ViewMode.CollectionItems }, ViewMode.CollectionItems, options.TerminalWidth, options.CacheProgress, options.CacheUsagePercent, layoutVariantLabel: options.LayoutVariantLabel);
        RenderPreloadDetailOverlay(options);
    }

    public void RenderLauncher(List<Bookmark> bookmarks, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _launcherRenderer.RenderLauncher(bookmarks, selectedIndex, scrollOffset, options);
        _helpers.PositionAtBottom();
        _launcherRenderer.RenderFooter(options.TerminalWidth, options.ScheduledRunBadge);
        RenderPreloadDetailOverlay(options);
    }

    public void RenderStatusBar(NavigationContext context, ViewMode mode)
    {
        _statusBarRenderer.RenderStatusBar(context, mode);
    }

    public void Clear()
    {
        _helpers.Clear();
    }

    /// <summary>
    /// Starts buffering all subsequent helper writes into a single frame so the
    /// orchestrator can emit it atomically (workspace-1f5a).
    /// </summary>
    public void BeginFrame() => _helpers.BeginFrame();

    /// <summary>
    /// Flushes the buffered frame as a single Console.Out.Write + Flush.
    /// </summary>
    public void EndFrame() => _helpers.EndFrame();

    internal void SetParagraphSpans(IReadOnlyList<LineCacheManager.ParagraphSpan>? spans) => _paragraphSpans = spans;

    /// <summary>
    /// Renders the prefetch detail overlay (workspace-v75w) when the toggle is
    /// active and there is preload progress to show. No-op otherwise so the
    /// existing view paints unchanged.
    /// </summary>
    private void RenderPreloadDetailOverlay(RenderOptions options)
    {
        if (!options.ShowPreloadDetail || options.CacheProgress == null)
        {
            return;
        }

        // workspace-c8v3: the panel is an OVERLAY — it must NOT call _helpers.Clear()
        // (resets cursor + _linesWritten) or _helpers.ClearRemainingLines() (writes
        // \x1b[K from _linesWritten downward, wiping the status bar already painted
        // by the calling view). Both are layout-management ops that conflict with
        // the "render on top of existing chrome" contract. Position absolutely
        // instead.
        _preloadDetailRenderer.Render(options.CacheProgress, options.TerminalWidth, options.TerminalHeight);
    }

    /// <summary>
    /// Renders the active toast notification as an overlay in the top-right corner.
    /// Called after the main content and status bar have been rendered.
    /// </summary>
    private void RenderToastOverlay(NavigationContext context, int terminalWidth)
    {
        if (context.ActiveToast == null)
        {
            return;
        }

        var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        ToastRenderer.RenderToast(context.ActiveToast, palette, terminalWidth, _helpers);
    }

    /// <summary>
    /// Renders an inline rounded box for the "extraction failed" state in reader view.
    /// Unlike RenderCenteredBox, this preserves the header that was already drawn —
    /// it just emits a centered, WarningFg-bordered box at the current cursor position
    /// with verb-led copy and the four recovery key bindings (workspace-d799).
    /// </summary>
    private void RenderExtractionFailureBox(int terminalWidth)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        var lines = new List<CenteredBoxLine>
        {
            CenteredBoxLine.Empty,
            new($"{p.PrimaryText.AnsiFg}Couldn't extract this article.{Reset}", "Couldn't extract this article."),
            new($"{p.SecondaryText.AnsiFg}Try a fresh fetch, or open it in your browser.{Reset}", "Try a fresh fetch, or open it in your browser."),
            CenteredBoxLine.Empty,
            new(
                $"{p.GetAccentFg().AnsiFg}o{Reset}{p.SecondaryText.AnsiFg}:open in browser{Reset}",
                "o:open in browser"),
            new(
                $"{p.GetAccentFg().AnsiFg}Shift+R{Reset}{p.SecondaryText.AnsiFg}:refresh{Reset}",
                "Shift+R:refresh"),
            new(
                $"{p.GetAccentFg().AnsiFg}Shift+I{Reset}{p.SecondaryText.AnsiFg}:headed re-fetch{Reset}",
                "Shift+I:headed re-fetch"),
            new(
                $"{p.GetAccentFg().AnsiFg}v{Reset}{p.SecondaryText.AnsiFg}:toggle view{Reset}",
                "v:toggle view"),
            CenteredBoxLine.Empty,
        };

        // Box width = longest content + 4 (2 border chars + 2 padding spaces).
        var maxContentWidth = 0;
        foreach (var line in lines)
        {
            var w = RenderHelpers.GetDisplayWidth(line.PlainText);
            if (w > maxContentWidth)
            {
                maxContentWidth = w;
            }
        }

        var innerWidth = Math.Max(MinBoxWidth - 4, maxContentWidth + 2);
        var boxWidth = innerWidth + 4;
        var leftPad = Math.Max(0, (terminalWidth - boxWidth) / 2);
        var pad = new string(' ', leftPad);
        var borderFg = p.GetWarningFg().AnsiFg;

        // Top border
        _helpers.WriteLine($"{pad}{borderFg}╭{new string('─', boxWidth - 2)}╮{Reset}");

        foreach (var line in lines)
        {
            var displayWidth = RenderHelpers.GetDisplayWidth(line.PlainText);
            var rightPadding = Math.Max(0, innerWidth - displayWidth);
            _helpers.WriteLine($"{pad}{borderFg}│{Reset} {line.StyledText}{new string(' ', rightPadding)} {borderFg}│{Reset}");
        }

        _helpers.WriteLine($"{pad}{borderFg}╰{new string('─', boxWidth - 2)}╯{Reset}");
    }

    /// <summary>
    /// Renders a centered rounded box with the given content lines.
    /// The box is horizontally centered and positioned at 1/3 from the top.
    /// </summary>
    private void RenderCenteredBox(List<CenteredBoxLine> lines, ThemeColor borderColor)
    {
        // Calculate box width from longest content line + 4 (2 border chars + 2 padding)
        var maxContentWidth = 0;
        foreach (var line in lines)
        {
            var w = RenderHelpers.GetDisplayWidth(line.PlainText);
            if (w > maxContentWidth)
            {
                maxContentWidth = w;
            }
        }

        // Box inner width is content + 2 spaces padding per side
        var innerWidth = Math.Max(MinBoxWidth - 4, Math.Min(maxContentWidth + 2, MaxBoxContentWidth));
        var boxWidth = innerWidth + 4; // 2 border chars + 2 padding spaces

        int termWidth;
        int termHeight;
        try
        {
            termWidth = Console.WindowWidth;
            termHeight = _helpers.TerminalHeight;
        }
        catch
        {
            termWidth = 80;
            termHeight = 24;
        }

        var leftPad = Math.Max(0, (termWidth - boxWidth) / 2);
        var boxHeight = lines.Count + 2; // content lines + top/bottom borders
        var topPad = Math.Max(0, (termHeight - boxHeight) / 3);
        var pad = new string(' ', leftPad);
        var borderFg = borderColor.AnsiFg;

        _helpers.Clear();

        // Top padding
        for (var i = 0; i < topPad; i++)
        {
            _helpers.WriteLine();
        }

        // Top border: ╭────────╮
        _helpers.WriteLine($"{pad}{borderFg}\u256d{new string('\u2500', boxWidth - 2)}\u256e{Reset}");

        // Content lines
        foreach (var line in lines)
        {
            var displayWidth = RenderHelpers.GetDisplayWidth(line.PlainText);
            var rightPadding = Math.Max(0, innerWidth - displayWidth);
            _helpers.WriteLine($"{pad}{borderFg}\u2502{Reset} {line.StyledText}{new string(' ', rightPadding)} {borderFg}\u2502{Reset}");
        }

        // Bottom border: ╰────────╯
        _helpers.WriteLine($"{pad}{borderFg}\u2570{new string('\u2500', boxWidth - 2)}\u256f{Reset}");

        _helpers.ClearRemainingLines();
    }

#pragma warning disable SA1204 // Static helpers kept after the methods that use them for readability.
    private static string StyleHintLine(string hint, ThemePalette p)
    {
        // Style recognised key tokens (Shift+O, Shift+R, Shift+I, Enter, b, Esc) in AccentFg
        // and the surrounding ":verb" descriptors in DimFg/SecondaryText. Falls back to a
        // single-color render when the hint contains no recognised tokens.
        var styled = hint;
        var tokens = new[] { "Shift+O", "Shift+R", "Shift+I", "Enter", "Esc" };
        foreach (var token in tokens)
        {
            if (!styled.Contains(token, StringComparison.Ordinal))
            {
                continue;
            }

            styled = styled.Replace(token, $"{p.GetAccentFg().AnsiFg}{token}{Reset}{p.SecondaryText.AnsiFg}", StringComparison.Ordinal);
        }

        return $"{p.SecondaryText.AnsiFg}{styled}{Reset}";
    }

    private static string ExtractDomainSafe(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }
#pragma warning restore SA1204

    /// <summary>
    /// Represents a line inside a centered box, carrying both styled (ANSI) and plain text
    /// so the box can measure display width from plain text while rendering styled text.
    /// </summary>
    private readonly record struct CenteredBoxLine(string StyledText, string PlainText)
    {
        /// <summary>An empty line (no text content).</summary>
        public static CenteredBoxLine Empty => new(string.Empty, string.Empty);
    }
}
