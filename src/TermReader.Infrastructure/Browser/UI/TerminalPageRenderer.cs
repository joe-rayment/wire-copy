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
    private readonly IThemeProvider _themeProvider;
    private readonly RenderHelpers _helpers;
    private readonly LinkTreeRenderer _linkTreeRenderer;
    private readonly ArticleRenderer _articleRenderer;
    private readonly CollectionRenderer _collectionRenderer;
    private readonly LauncherRenderer _launcherRenderer;
    private readonly StatusBarRenderer _statusBarRenderer;

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

        _linkTreeRenderer.RenderHeader(page.Metadata, page.Url, options);

        var remainingHeight = Math.Max(3, options.TerminalHeight - _helpers.LinesWritten - 3);

        if (page.LinkTree != null)
        {
            _linkTreeRenderer.RenderLinkTree(page.LinkTree, context, remainingHeight, options);
        }
        else
        {
            _helpers.WriteLine();
            _helpers.WriteLine("  No links found on this page.");
            if (page.ReadableContent != null)
            {
                _helpers.WriteLine("  Press 'r' to switch to reader view.");
            }

            _helpers.WriteLine();
        }

        _statusBarRenderer.RenderStatusBar(context, ViewMode.Hierarchical, options.TerminalWidth, options.CacheProgress, options.CacheUsagePercent);

        _helpers.ClearRemainingLines();
    }

    public void RenderReadable(Page page, NavigationContext context, RenderOptions options, List<string>? wrappedLines = null)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();

        if (page.ReadableContent == null)
        {
            _helpers.WriteLine();
            _helpers.WriteLine("  No readable content available for this page.");
            _helpers.WriteLine("  Press 'v' to switch to link view.");
            _helpers.WriteLine();
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth);
            _helpers.ClearRemainingLines();
            return;
        }

        var viewportHeight = Math.Max(3, options.TerminalHeight - _helpers.LinesWritten - 3);

        if (wrappedLines != null)
        {
            var focusLineOffset = Math.Max(0, Math.Min(viewportHeight / 3, wrappedLines.Count - context.ScrollOffset - 1));
            _articleRenderer.RenderLineBasedContent(wrappedLines, context, viewportHeight, options, focusLineOffset);
            _articleRenderer.RenderReaderStatusBar(context, wrappedLines.Count, options.MaxContentWidth, viewportHeight, options.TerminalWidth);
        }
        else
        {
            _articleRenderer.RenderArticleContent(page.ReadableContent, context, viewportHeight, options);
            _statusBarRenderer.RenderStatusBar(context, ViewMode.Readable, options.TerminalWidth);
        }

        _helpers.ClearRemainingLines();
    }

    public void RenderLoading(string url)
    {
        _helpers.Clear();
        _helpers.WriteLine();
        _helpers.WriteLine("  Loading...");
        _helpers.WriteLine($"  {RenderHelpers.TruncateUrl(url, 70)}");
        _helpers.WriteLine();
        _helpers.ClearRemainingLines();
    }

    public void RenderError(string message, string url)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        _helpers.Clear();
        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.ErrorFg.AnsiFg}Error loading page:\x1b[0m");
        _helpers.WriteLine($"  {message}");
        _helpers.WriteLine();
        _helpers.WriteLine($"  URL: {RenderHelpers.TruncateUrl(url, 60)}");
        _helpers.WriteLine();
        _helpers.WriteLine("  Press 'b' to go back or 'q' to quit.");
        _helpers.WriteLine();
        _helpers.ClearRemainingLines();
    }

    public void RenderChallenge(string url)
    {
        _helpers.Clear();
        _helpers.WriteLine();
        _helpers.WriteLine("  Bot challenge detected. Please solve it in the browser window.");
        _helpers.WriteLine($"  URL: {RenderHelpers.TruncateUrl(url, 60)}");
        _helpers.WriteLine();
        _helpers.WriteLine("  Waiting for challenge to be resolved...");
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

    public void RenderCollectionList(List<Collection> collections, int selectedIndex, Guid? defaultCollectionId, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _collectionRenderer.RenderCollectionList(collections, selectedIndex, defaultCollectionId, scrollOffset, options);
        _statusBarRenderer.RenderStatusBar(new NavigationContext { ViewMode = ViewMode.CollectionList }, ViewMode.CollectionList, options.TerminalWidth);
        _helpers.ClearRemainingLines();
    }

    public void RenderCollectionItems(Collection collection, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _collectionRenderer.RenderCollectionItems(collection, selectedIndex, scrollOffset, options);
        _statusBarRenderer.RenderStatusBar(new NavigationContext { ViewMode = ViewMode.CollectionItems }, ViewMode.CollectionItems, options.TerminalWidth, options.CacheProgress, options.CacheUsagePercent);
        _helpers.ClearRemainingLines();
    }

    public void RenderLauncher(List<Bookmark> bookmarks, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        _helpers.TerminalHeight = options.TerminalHeight;
        _helpers.Clear();
        _launcherRenderer.RenderLauncher(bookmarks, selectedIndex, scrollOffset, options);
        _launcherRenderer.RenderFooter(options.TerminalWidth);
        _helpers.ClearRemainingLines();
    }

    public void RenderStatusBar(NavigationContext context, ViewMode mode)
    {
        _statusBarRenderer.RenderStatusBar(context, mode);
    }

    public void Clear()
    {
        _helpers.Clear();
    }
}
