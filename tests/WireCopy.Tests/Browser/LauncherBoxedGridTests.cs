// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression tests for workspace-bs93: the launcher Grid variant renders each
/// bookmark as a link-list-style card (blank pad + title + subtitle + thin
/// separator rule) with a right-aligned [N] digit badge. Selection is signalled
/// by an accent bar (▌) on the title and subtitle rows plus a background
/// highlight. The inter-column divider matches the link list (`│` on content
/// rows, `┼` on the separator row). Box-drawing borders from the pre-bs93
/// design (`╭ ╮ ╰ ╯`) are gone — both views now share the same vocabulary so
/// the product reads as coherent. Digits `1`-`9` still emit a JumpToIndex
/// command (lower fixture).
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class LauncherBoxedGridTests
{
    private const int LargeTerminalWidth = 100;
    private const int TerminalHeight = 35;

    [Fact]
    public void Grid_DoesNotRenderClosedBoxBorders_OnBookmarkCells()
    {
        // workspace-bs93: bookmark cells no longer use box-drawing borders.
        // The wordmark/header card and the URL bar each still use corners
        // (╭ ╮ ╰ ╯), so up to 2 of each is expected. A per-cell box would
        // emit many more — 4 cards × 2 top corners = at least 8 of each.
        var raw = RenderLauncherCapture(CreateBookmarks(4), selectedIndex: -1);

        CountOccurrences(raw, "╭").Should().BeLessOrEqualTo(2, "only the header card and URL bar may have a top-left corner");
        CountOccurrences(raw, "╮").Should().BeLessOrEqualTo(2, "only the header card and URL bar may have a top-right corner");
        CountOccurrences(raw, "╰").Should().BeLessOrEqualTo(2, "only the header card and URL bar may have a bottom-left corner");
        CountOccurrences(raw, "╯").Should().BeLessOrEqualTo(2, "only the header card and URL bar may have a bottom-right corner");
    }

    [Fact]
    public void Grid_RendersAccentBar_OnSelectedCell()
    {
        // workspace-bs93: selection is signalled by an accent bar `▌` on the
        // title and subtitle rows plus a background highlight — matching the
        // link-list card.
        var raw = RenderLauncherCapture(CreateBookmarks(2), selectedIndex: 0);

        raw.Should().Contain("▌",
            "selected bookmark cells must show the link-list-style accent bar");
    }

    [Fact]
    public void Grid_RightAlignsDigitBadge_OnTitleLine()
    {
        var raw = RenderLauncherCapture(CreateBookmarks(2), selectedIndex: -1);

        // workspace-bs93: badge ends with a trailing space — no more right box
        // border. Slot layout (workspace-ul5z): bookmark[0] at slot 0 → [1],
        // Reading List at slot 1 → [2], bookmark[1] at slot 2 → [3].
        var stripped = StripAnsi(raw);
        stripped.Should().Contain("[1] ",
            "digit badge [1] must be right-aligned with a trailing space");
        stripped.Should().Contain("[2] ",
            "digit badge [2] must be right-aligned with a trailing space");
        stripped.Should().Contain("[3] ",
            "digit badge [3] must be right-aligned with a trailing space");
    }

    [Fact]
    public void Grid_NoBadge_For10thBookmarkAndBeyond()
    {
        var raw = RenderLauncherCapture(CreateBookmarks(12), selectedIndex: -1);
        var stripped = StripAnsi(raw);

        // Items 10+ render no badge (same as the pre-redesign launcher).
        stripped.Should().NotContain("[10]");
        stripped.Should().NotContain("[12]");
    }

    [Fact]
    public void Grid_LayoutCellHeight_IsFourLineCardStride()
    {
        // workspace-bs93: card cell stride is 4 lines — blank pad + title +
        // subtitle + separator rule. Adjacent cards stack directly; the
        // separator provides the visual break, matching link-list cards.
        // Scroll math relies on this stride to keep selected cells fully in
        // the viewport.
        var layout = LauncherRenderer.ComputeLayout(LargeTerminalWidth, TerminalHeight, "Grid");
        layout.CellHeight.Should().Be(4);
    }

    [Fact]
    public void Grid_HasVerticalDivider_BetweenColumns_ToMatchLinkList()
    {
        // workspace-bs93: the launcher now mirrors the link-list's inter-column
        // divider: `│` on content rows and `┼` on the separator row.
        var stripped = StripAnsi(RenderLauncherCapture(CreateBookmarks(4), selectedIndex: -1));

        stripped.Should().Contain("│",
            "two-column launcher cards must use a │ divider between columns, matching the link list");
        stripped.Should().Contain("┼",
            "the divider must transition to ┼ on the separator row so the bottom rule reads as continuous");
    }

    [Fact]
    public void Grid_SeparatorRule_RendersBetweenCards()
    {
        // workspace-bs93: each card ends with a `─` rule across its width —
        // the visual separator between adjacent cards, replacing the old
        // boxed border + blank-gutter design.
        var stripped = StripAnsi(RenderLauncherCapture(CreateBookmarks(2), selectedIndex: -1));

        stripped.Should().Contain("─",
            "each card must end with a thin separator rule matching the link-list card");
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
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

    private static string RenderLauncherCapture(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int scrollOffset = 0,
        string variant = "Grid")
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = TerminalHeight };
        var renderer = new LauncherRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = LargeTerminalWidth,
            TerminalHeight = TerminalHeight,
            LayoutVariant = variant,
        };

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderLauncher(bookmarks, selectedIndex, scrollOffset, options);
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string StripAnsi(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"\x1b\[[0-9;]*m", string.Empty);
    }
}

/// <summary>
/// Regression tests for workspace-wxht: digit `1`-`9` keypresses on the
/// launcher emit a <c>JumpToIndex</c> command (count = digit) instead of
/// accumulating into a numeric prefix. The handler clamps to the bookmark
/// count and snaps the selection accordingly. Out-of-range digits are no-ops.
/// </summary>
[Trait("Category", "Unit")]
public class LauncherJumpToIndexTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private bool _renderCalled;

    public LauncherJumpToIndexTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new PageMetadata { Title = "Test" });
        page.SetReadableContent(ReadableContent.Create(
            "Test", "Test content", new List<string> { "Paragraph 1" }));
        _navigationService.NavigateTo(page);
        _navigationService.EnterLauncher();

        _options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 35,
            LayoutVariant = "Grid",
        };

        var serviceProvider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var lineCacheManager = new LineCacheManager(_navigationService, themeProvider);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
            RenderCurrentPageAsync = (_, _) =>
            {
                _renderCalled = true;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    private static List<Bookmark> CreateBookmarks(int count)
    {
        var list = new List<Bookmark>();
        for (var i = 0; i < count; i++)
        {
            list.Add(Bookmark.Create($"Bookmark {i + 1}", $"https://example.com/{i + 1}", i));
        }

        return list;
    }

    [Fact]
    public async Task JumpToIndex_Digit1_With3Bookmarks_SelectsFirstBookmark()
    {
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 2;
        _renderCalled = false;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.JumpToIndex, Count = 1, RawKeyChar = '1' },
            _options,
            CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(0);
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task JumpToIndex_Digit5_With3Bookmarks_IsNoOp()
    {
        // Total slots with 3 bookmarks = 4 (Reading List + 3 bookmarks).
        // Digit 5 → virtual 4 is out of range.
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 1;
        _renderCalled = false;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.JumpToIndex, Count = 5, RawKeyChar = '5' },
            _options,
            CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(1,
            "out-of-range digit must not move the selection");
        _renderCalled.Should().BeFalse(
            "out-of-range digit must not trigger a re-render");
    }

    [Fact]
    public async Task JumpToIndex_Digit2_With3Bookmarks_SelectsReadingListSlot()
    {
        // Reading List sits at virtual index 1 (workspace-ul5z) so digit `2`
        // (digit-1 → virtual 1) addresses it directly.
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 0;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.JumpToIndex, Count = 2, RawKeyChar = '2' },
            _options,
            CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(1,
            "digit 2 must jump to the reserved Reading List slot at virtual index 1");
    }

    [Fact]
    public async Task JumpToIndex_Digit3_With3Bookmarks_SelectsBookmarkOne()
    {
        // With Reading List at slot 1, bookmark[1] now lives at slot 2, so
        // digit `3` (count - 1 = 2) addresses it.
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 0;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.JumpToIndex, Count = 3, RawKeyChar = '3' },
            _options,
            CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(2,
            "digit 3 must jump to bookmark[1] (slot 2) under the Reading-List-at-slot-1 layout");
    }

    [Fact]
    public async Task JumpToIndex_Digit4_With3Bookmarks_SelectsLastBookmark()
    {
        // Total slots with 3 bookmarks = 4 (3 bookmarks + Reading List).
        // Digit 4 → virtual 3 → bookmark[2] (last bookmark).
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 0;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.JumpToIndex, Count = 4, RawKeyChar = '4' },
            _options,
            CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(3);
    }
}
