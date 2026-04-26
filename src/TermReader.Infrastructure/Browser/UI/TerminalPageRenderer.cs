// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;
using TermReader.Infrastructure.Browser.UI.Renderers;

namespace TermReader.Infrastructure.Browser.UI;

/// <summary>
/// Renders pages to the terminal.
/// Thin dispatcher that delegates to view-specific sub-renderers.
/// </summary>
public class TerminalPageRenderer : IPageRenderer
{
    private const string Reset = "\x1b[0m";
    private const int MinBoxWidth = 30;
    private const int MaxBoxContentWidth = 46;
    private static readonly char[] SpinnerFrames = ['\u280B', '\u2819', '\u2839', '\u2838', '\u283C', '\u2834', '\u2826', '\u2827', '\u2807', '\u280F'];

    private readonly IThemeProvider _themeProvider;
    private readonly RenderHelpers _helpers;
    private readonly LinkTreeRenderer _linkTreeRenderer;
    private readonly ArticleRenderer _articleRenderer;
    private readonly CollectionRenderer _collectionRenderer;
    private readonly LauncherRenderer _launcherRenderer;
    private readonly StatusBarRenderer _statusBarRenderer;
    private IReadOnlyList<LineCacheManager.ParagraphSpan>? _paragraphSpans;

    public TerminalPageRenderer(IThemeProvider themeProvider, ILogger<TerminalPageRenderer> logger)
    {
        _themeProvider = themeProvider;
        _helpers = new RenderHelpers();
        _linkTreeRenderer = new LinkTreeRenderer(_helpers, themeProvider);
        _articleRenderer = new ArticleRenderer(_helpers, themeProvider);
        _collectionRenderer = new CollectionRenderer(_helpers, themeProvider);
        _launcherRenderer = new LauncherRenderer(_helpers, themeProvider);
        _statusBarRenderer = new StatusBarRenderer(_helpers, themeProvider);
    }

    public void RenderHierarchical(Page page, NavigationContext context, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();

        var linkCount = page.LinkTree?.TotalLinks ?? 0;
        var sectionCount = page.LinkTree?.Root.Children.Count(c => c.IsGroupHeader) ?? 0;
        _linkTreeRenderer.RenderHeader(page.Metadata, page.Url, options, linkCount, sectionCount);

        var remainingHeight = Math.Max(3, options.TerminalHeight - _helpers.LinesWritten - 1);

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
        _statusBarRenderer.RenderStatusBar(context, ViewMode.Hierarchical, options.TerminalWidth, options.CacheProgress, options.CacheUsagePercent, layoutVariantLabel: options.LayoutVariantLabel);
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
                _helpers.WriteLine("  No readable content available for this page.");
                _helpers.WriteLine("  Press 'v' to switch to link view.");
            }

            _helpers.PositionAtBottom();
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth, layoutVariantLabel: options.LayoutVariantLabel);
            RenderToastOverlay(context, options.TerminalWidth);
            return;
        }

        // Reserve 2 lines: 1 for separator rule + 1 for anchored status bar
        var viewportHeight = Math.Max(3, options.TerminalHeight - _helpers.LinesWritten - 2);
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
                layoutVariantLabel: options.LayoutVariantLabel);
        }
        else
        {
            _articleRenderer.RenderArticleContent(page.ReadableContent, context, viewportHeight, options);
            _helpers.LeftMargin = 0;
            _helpers.RenderEndOfContentRule(palette, options.TerminalWidth);
            _helpers.PositionAtBottom();
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth, layoutVariantLabel: options.LayoutVariantLabel);
        }

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
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        var lines = new List<CenteredBoxLine>
        {
            CenteredBoxLine.Empty,
            new($"{p.GetWarningFg().AnsiFg}\u2847{Reset} {p.PrimaryText.AnsiFg}Bot challenge detected{Reset}", "\u2847 Bot challenge detected"),
            CenteredBoxLine.Empty,
            new($"{p.SecondaryText.AnsiFg}Waiting for manual intervention...{Reset}", "Waiting for manual intervention..."),
            CenteredBoxLine.Empty,
        };

        RenderCenteredBox(lines, p.GetWarningFg());
    }

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
    }

    public void RenderLauncher(List<Bookmark> bookmarks, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _launcherRenderer.RenderLauncher(bookmarks, selectedIndex, scrollOffset, options);
        _helpers.PositionAtBottom();
        _launcherRenderer.RenderFooter(options.TerminalWidth);
    }

    public void RenderStatusBar(NavigationContext context, ViewMode mode)
    {
        _statusBarRenderer.RenderStatusBar(context, mode);
    }

    public void Clear()
    {
        _helpers.Clear();
    }

    internal void SetParagraphSpans(IReadOnlyList<LineCacheManager.ParagraphSpan>? spans) => _paragraphSpans = spans;

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
        ToastRenderer.RenderToast(context.ActiveToast, palette, terminalWidth);
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
