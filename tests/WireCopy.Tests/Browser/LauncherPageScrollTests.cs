// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression tests for workspace-a1ua: the launcher renders wordmark, URL bar,
/// and bookmark grid as a single virtual content stream so the wordmark and
/// URL bar scroll off the top as the user navigates down through the list.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class LauncherPageScrollTests
{
    // termtest harness defaults — large wordmark variant.
    private const int LargeTerminalWidth = 100;
    private const int TerminalHeight = 24;
    private const int NarrowTerminalWidth = 80;

    private const string WordmarkSignature = "████";
    private const string UrlBarPlaceholderActive = "Type a URL and press Enter";
    private const string UrlBarPlaceholderInactive = "Go to URL…";

    [Fact]
    public void ScrollOffset_Zero_RendersWordmarkAndUrlBarAndItems()
    {
        var capture = RenderLauncherCapture(
            CreateBookmarks(15),
            selectedIndex: 0,
            scrollOffset: 0,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Grid");

        capture.RawOutput.Should().Contain(WordmarkSignature, "wordmark must be on screen at scroll offset 0");
        capture.RawOutput.Should().Contain(UrlBarPlaceholderInactive, "URL bar must be on screen at scroll offset 0");
        capture.RawOutput.Should().Contain("BOOKMARK 1", "at least one bookmark must be visible");
    }

    [Fact]
    public void ScrollOffset_PastWordmark_HidesWordmarkAndShowsBookmarks()
    {
        // URL bar is 4 rows after workspace-0rde compression.
        var headerLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(LargeTerminalWidth) - 4;

        var capture = RenderLauncherCapture(
            CreateBookmarks(20),
            selectedIndex: 4,
            scrollOffset: headerLines,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Grid");

        capture.RawOutput.Should().NotContain(WordmarkSignature,
            "the wordmark must scroll off the top once the page is scrolled past its last line");
    }

    [Fact]
    public void ScrollOffset_PastUrlBar_RendersOnlyItems()
    {
        var combinedHeaderLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(LargeTerminalWidth);

        var capture = RenderLauncherCapture(
            CreateBookmarks(30),
            selectedIndex: 8,
            scrollOffset: combinedHeaderLines,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Grid");

        capture.RawOutput.Should().NotContain(WordmarkSignature,
            "wordmark must not be visible once we have scrolled past the entire header region");
        capture.RawOutput.Should().NotContain(UrlBarPlaceholderInactive,
            "URL bar placeholder must not be visible once we have scrolled past it");
        capture.RawOutput.Should().NotContain(UrlBarPlaceholderActive);
    }

    [Fact]
    public void ScrollOffset_AdjustsToKeepSelectedBookmarkInViewport_Grid()
    {
        var ctx = CreateContext(
            CreateBookmarks(40),
            selectedIndex: 30,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Grid",
            scrollOffset: 0);

        InvokeAdjustLauncherScroll(ctx.Ctx, ctx.Options);

        var layout = LauncherRenderer.ComputeLayout(LargeTerminalWidth, TerminalHeight, "Grid");
        var headerLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(LargeTerminalWidth);
        var viewport = LauncherRenderer.ComputeViewportHeight(TerminalHeight);

        var selectedRow = 30 / layout.Columns;
        var cellTopLine = headerLines + (selectedRow * layout.CellHeight);
        var cellBottomLine = cellTopLine + layout.CellHeight - 1;

        var off = ctx.Ctx.NavigationService.LauncherScrollOffset;
        cellTopLine.Should().BeGreaterOrEqualTo(off, "selected cell must not be above the viewport");
        cellBottomLine.Should().BeLessThan(off + viewport, "selected cell must not be below the viewport");
    }

    [Fact]
    public void ScrollOffset_AdjustsToKeepSelectedBookmarkInViewport_List()
    {
        var ctx = CreateContext(
            CreateBookmarks(40),
            selectedIndex: 30,
            terminalWidth: NarrowTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "List",
            scrollOffset: 0);

        InvokeAdjustLauncherScroll(ctx.Ctx, ctx.Options);

        var headerLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(NarrowTerminalWidth);
        var viewport = LauncherRenderer.ComputeViewportHeight(TerminalHeight);

        var cellTopLine = headerLines + 30;
        var cellBottomLine = cellTopLine;

        var off = ctx.Ctx.NavigationService.LauncherScrollOffset;
        cellTopLine.Should().BeGreaterOrEqualTo(off);
        cellBottomLine.Should().BeLessThan(off + viewport);
    }

    [Fact]
    public void ScrollOffset_AdjustsToKeepSelectedBookmarkInViewport_Compact()
    {
        var ctx = CreateContext(
            CreateBookmarks(40),
            selectedIndex: 30,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Compact",
            scrollOffset: 0);

        InvokeAdjustLauncherScroll(ctx.Ctx, ctx.Options);

        var layout = LauncherRenderer.ComputeLayout(LargeTerminalWidth, TerminalHeight, "Compact");
        var headerLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(LargeTerminalWidth);
        var viewport = LauncherRenderer.ComputeViewportHeight(TerminalHeight);

        var selectedRow = 30 / layout.Columns;
        var cellTopLine = headerLines + (selectedRow * layout.CellHeight);
        var cellBottomLine = cellTopLine + layout.CellHeight - 1;

        var off = ctx.Ctx.NavigationService.LauncherScrollOffset;
        cellTopLine.Should().BeGreaterOrEqualTo(off);
        cellBottomLine.Should().BeLessThan(off + viewport);
    }

    [Fact]
    public void UrlBarFocus_ResetsScrollOffset_SoUrlBarVisible()
    {
        var ctx = CreateContext(
            CreateBookmarks(40),
            selectedIndex: -1,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Grid",
            scrollOffset: 50);

        InvokeAdjustLauncherScroll(ctx.Ctx, ctx.Options);

        ctx.Ctx.NavigationService.LauncherScrollOffset.Should().Be(0,
            "page scroll must reset to 0 whenever the URL bar gains focus");
    }

    [Fact]
    public void FirstBookmark_Selection_SnapsScrollOffsetToTop_SoWordmarkReappears()
    {
        var ctx = CreateContext(
            CreateBookmarks(40),
            selectedIndex: 0,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Grid",
            scrollOffset: 50);

        InvokeAdjustLauncherScroll(ctx.Ctx, ctx.Options);

        ctx.Ctx.NavigationService.LauncherScrollOffset.Should().Be(0);
    }

    [Fact]
    public void Scrolling_DoesNotLoseFooter_FooterRenderedSeparately()
    {
        var capture = RenderLauncherCapture(
            CreateBookmarks(50),
            selectedIndex: 25,
            scrollOffset: 30,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Grid");

        var viewport = LauncherRenderer.ComputeViewportHeight(TerminalHeight);
        capture.LinesWritten.Should().BeLessOrEqualTo(viewport,
            "RenderLauncher must never spill into the footer region pinned at the bottom");
    }

    [Fact]
    public void List_Variant_ScrollOffset_HidesWordmark()
    {
        var combinedHeaderLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(LargeTerminalWidth);
        var capture = RenderLauncherCapture(
            CreateBookmarks(30),
            selectedIndex: 15,
            scrollOffset: combinedHeaderLines,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "List");

        capture.RawOutput.Should().NotContain(WordmarkSignature,
            "List variant must also collapse the wordmark when scrolled past the header region");
    }

    [Fact]
    public void Compact_Variant_ScrollOffset_HidesWordmark()
    {
        var combinedHeaderLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(LargeTerminalWidth);
        var capture = RenderLauncherCapture(
            CreateBookmarks(30),
            selectedIndex: 15,
            scrollOffset: combinedHeaderLines,
            terminalWidth: LargeTerminalWidth,
            terminalHeight: TerminalHeight,
            variant: "Compact");

        capture.RawOutput.Should().NotContain(WordmarkSignature,
            "Compact variant must also collapse the wordmark when scrolled past the header region");
    }

    [Fact]
    public void Layout_VisibleRows_WorstCase_NoScroll_FitsOnScreen()
    {
        var layout = LauncherRenderer.ComputeLayout(LargeTerminalWidth, TerminalHeight, "Grid");
        var headerLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(LargeTerminalWidth);
        var footerLines = layout.FooterLines;

        var bookmarkAreaLines = layout.VisibleRows * layout.CellHeight;

        // The taller Launcher.dc.html masthead (workspace-pn5f) can leave a
        // short terminal too little room for even ONE full-height tile; the
        // layout then floors VisibleRows at 1 and the launcher scrolls. The
        // fit invariant only applies when at least one cell genuinely fits.
        var available = TerminalHeight - headerLines - footerLines;
        if (available >= layout.CellHeight)
        {
            (headerLines + bookmarkAreaLines + footerLines).Should().BeLessOrEqualTo(TerminalHeight,
                "initial render must fit in the terminal: header + visible bookmark rows + footer ≤ terminalHeight");
        }
        else
        {
            layout.VisibleRows.Should().Be(1,
                "when not even one cell fits under the chrome, VisibleRows floors at 1 and the view scrolls");
        }
    }

    private static List<Bookmark> CreateBookmarks(int count)
    {
        var list = new List<Bookmark>();
        for (var i = 0; i < count; i++)
        {
            list.Add(Bookmark.Create($"bookmark {i + 1}", $"https://example.com/{i + 1}", i));
        }

        return list;
    }

    private static (string RawOutput, int LinesWritten) RenderLauncherCapture(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int scrollOffset,
        int terminalWidth,
        int terminalHeight,
        string variant)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
        var renderer = new LauncherRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = terminalWidth,
            TerminalHeight = terminalHeight,
            LayoutVariant = variant,
        };

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderLauncher(bookmarks, selectedIndex, scrollOffset, options);
            return (sw.ToString(), helpers.LinesWritten);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static (CommandContext Ctx, RenderOptions Options) CreateContext(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int terminalWidth,
        int terminalHeight,
        string variant,
        int scrollOffset)
    {
        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var nav = new NavigationService(navLogger);
        nav.EnterLauncher();
        nav.LauncherSelectedIndex = selectedIndex;
        nav.LauncherScrollOffset = scrollOffset;

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var options = new RenderOptions
        {
            TerminalWidth = terminalWidth,
            TerminalHeight = terminalHeight,
            LayoutVariant = variant,
        };

        var ctx = new CommandContext
        {
            NavigationService = nav,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(nav, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            Bookmarks = bookmarks,
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
            RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        return (ctx, options);
    }

    /// <summary>
    /// Invokes <c>LauncherCommandHandler.AdjustLauncherScroll</c> via reflection.
    /// </summary>
    private static void InvokeAdjustLauncherScroll(CommandContext ctx, RenderOptions options)
    {
        var method = typeof(LauncherCommandHandler).GetMethod(
            "AdjustLauncherScroll",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, new object[] { ctx, options });
    }
}
