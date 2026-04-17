// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Renderers;

namespace TermReader.Infrastructure.Browser.UI;

/// <summary>
/// Renders pages to the terminal.
/// Thin dispatcher that delegates to view-specific sub-renderers.
/// </summary>
public class TerminalPageRenderer : IPageRenderer
{
    private const string Reset = "\x1b[0m";
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
                _helpers.WriteLine($"  {ep.SecondaryText.AnsiFg}Try reader view \u2014 press{Reset} {ep.PrimaryText.AnsiFg}v{Reset}");
            }

            _helpers.WriteLine();
        }

        _helpers.PositionAtBottom();
        _statusBarRenderer.RenderStatusBar(context, ViewMode.Hierarchical, options.TerminalWidth, options.CacheProgress, options.CacheUsagePercent);
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
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth);
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
                readerViewportHeight: viewportHeight);
        }
        else
        {
            _articleRenderer.RenderArticleContent(page.ReadableContent, context, viewportHeight, options);
            _helpers.LeftMargin = 0;
            _helpers.RenderEndOfContentRule(palette, options.TerminalWidth);
            _helpers.PositionAtBottom();
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth);
        }
    }

    public void RenderLoading(string url, string? status = null)
    {
        RenderLoading(url, status, elapsedMs: 0);
    }

    public void RenderLoading(string url, string? status, long elapsedMs)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var label = status ?? "Loading...";
        _helpers.Clear();
        _helpers.WriteLine();

        // Animated spinner: cycles through 10 braille frames every 500ms
        var frameIndex = (int)((elapsedMs / 500) % SpinnerFrames.Length);
        var spinner = SpinnerFrames[frameIndex];

        // Elapsed seconds — visible proof the app is running
        var elapsed = elapsedMs >= 1000
            ? $"  {p.SecondaryText.AnsiFg}{elapsedMs / 1000}s{Reset}"
            : string.Empty;

        _helpers.WriteLine($"  {p.PromptFg.AnsiFg}{spinner}{Reset} {p.PrimaryText.AnsiFg}{label}{Reset}{elapsed}");
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{RenderHelpers.TruncateUrl(url, 70)}{Reset}");
        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.GetAccentFg().AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:cancel{Reset}");
        _helpers.WriteLine();
        _helpers.ClearRemainingLines();
    }

    public void RenderError(string message, string url)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        _helpers.Clear();

        // Title bar showing the URL that failed
        var domain = LauncherRenderer.ExtractDomain(url);
        var title = $"{p.ErrorFg.AnsiFg}\x1b[1mError\x1b[0m";
        var meta = $"{p.SecondaryText.AnsiFg}{domain}\x1b[0m";
        var titleLen = "Error".Length;
        var metaLen = domain.Length;
        var width = Math.Max(1, Console.WindowWidth - 2);
        var padding = Math.Max(1, width - 1 - titleLen - metaLen);
        _helpers.WriteLine($" {title}{new string(' ', padding)}{meta}");

        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.ErrorFg.AnsiFg}Something went wrong loading this page.{Reset}");
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{message}{Reset}");
        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{RenderHelpers.TruncateUrl(url, 65)}{Reset}");
        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Press{Reset} {p.PrimaryText.AnsiFg}b{Reset} {p.SecondaryText.AnsiFg}to go back or{Reset} {p.PrimaryText.AnsiFg}Shift+R{Reset} {p.SecondaryText.AnsiFg}to retry{Reset}");
        _helpers.WriteLine();
        _helpers.ClearRemainingLines();
    }

    public void RenderChallenge(string url)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        _helpers.Clear();
        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.GetWarningFg().AnsiFg}\u2847{Reset} {p.PrimaryText.AnsiFg}Bot challenge detected{Reset}");
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Please solve it in the browser window.{Reset}");
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{RenderHelpers.TruncateUrl(url, 65)}{Reset}");
        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Waiting for you to finish...{Reset}");
        _helpers.ClearRemainingLines();
    }

    public void RenderInteractiveRefresh(string url)
    {
        _helpers.Clear();
        _helpers.WriteLine();
        _helpers.WriteLine("  Interactive refresh — browser window is open.");
        _helpers.WriteLine($"  URL: {RenderHelpers.TruncateUrl(url, 60)}");
        _helpers.WriteLine();
        _helpers.WriteLine("  Complete any captcha or login in the browser window.");
        _helpers.WriteLine("  Press Enter to accept the page, or Esc to cancel.");
        _helpers.ClearRemainingLines();
    }

    public void RenderManualLogin(string url, string domain)
    {
        _helpers.Clear();
        _helpers.WriteLine();
        _helpers.WriteLine($"  Login required for {domain}. Please log in via the browser window.");
        _helpers.WriteLine($"  URL: {RenderHelpers.TruncateUrl(url, 60)}");
        _helpers.WriteLine();
        _helpers.WriteLine("  Waiting for login to complete...");
        _helpers.ClearRemainingLines();
    }

    public void RenderCollectionList(List<Collection> collections, int selectedIndex, Guid? defaultCollectionId, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _collectionRenderer.RenderCollectionList(collections, selectedIndex, defaultCollectionId, scrollOffset, options);
        _helpers.PositionAtBottom();
        _statusBarRenderer.RenderStatusBar(new NavigationContext { ViewMode = ViewMode.CollectionList }, ViewMode.CollectionList, options.TerminalWidth);
    }

    public void RenderCollectionItems(Collection collection, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _collectionRenderer.RenderCollectionItems(collection, selectedIndex, scrollOffset, options);
        _helpers.PositionAtBottom();
        _statusBarRenderer.RenderStatusBar(new NavigationContext { ViewMode = ViewMode.CollectionItems }, ViewMode.CollectionItems, options.TerminalWidth, options.CacheProgress, options.CacheUsagePercent);
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
}
