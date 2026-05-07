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
/// Regression tests for workspace-wxht: the launcher Grid variant renders each
/// bookmark as a closed thin-box cell with a right-aligned [N] digit badge,
/// drops the per-cell `▌` accent bar in favour of brightening the selected
/// cell's border, drops the inter-column `│` divider in favour of a 1-cell
/// horizontal gutter (and a 1-line vertical gutter between rows), and wires
/// digits 1-9 in launcher view to a JumpToIndex command instead of consuming
/// them as a numeric prefix.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class LauncherBoxedGridTests
{
    private const int LargeTerminalWidth = 100;
    private const int TerminalHeight = 35;

    [Fact]
    public void Grid_RendersClosedBoxBorders_TopAndBottomGlyphs()
    {
        var raw = RenderLauncherCapture(CreateBookmarks(2), selectedIndex: -1);

        raw.Should().Contain("╭", "top-left rounded corner glyph must be present");
        raw.Should().Contain("╮", "top-right rounded corner glyph must be present");
        raw.Should().Contain("╰", "bottom-left rounded corner glyph must be present");
        raw.Should().Contain("╯", "bottom-right rounded corner glyph must be present");
    }

    [Fact]
    public void Grid_DoesNotRenderAccentBar_OnSelectedCell()
    {
        // The pre-redesign launcher rendered '▌' (U+258C) as the left-edge
        // accent bar on the selected cell. With every cell now bordered, the
        // bar is gone — the brightened border is the selection signal.
        var raw = RenderLauncherCapture(CreateBookmarks(2), selectedIndex: 0);

        raw.Should().NotContain("▌",
            "the per-cell accent bar must be removed in the boxed Grid path; " +
            "selection is signalled by brightening the border colour");
    }

    [Fact]
    public void Grid_RightAlignsDigitBadge_OnTitleLine()
    {
        var raw = RenderLauncherCapture(CreateBookmarks(2), selectedIndex: -1);

        // Title ends with a `[N]` token followed by a single padding space and
        // the right border. Strip ANSI then look for that signature.
        var stripped = StripAnsi(raw);
        stripped.Should().Contain("[1] │",
            "digit badge [1] must be right-aligned next to the right box border");
        stripped.Should().Contain("[2] │",
            "digit badge [2] must be right-aligned next to the right box border");
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
    public void Grid_LayoutCellHeight_IsFiveLineStride()
    {
        // 4 visible box lines (top + title + url + bottom) plus 1 inter-row
        // blank line = 5-line stride per row. Scroll math relies on this
        // stride to keep selected cells fully in the viewport.
        var layout = LauncherRenderer.ComputeLayout(LargeTerminalWidth, TerminalHeight, "Grid");
        layout.CellHeight.Should().Be(5);
    }

    [Fact]
    public void Grid_NoVerticalDivider_BetweenColumns()
    {
        // The previous design drew a `│` divider between the two columns,
        // independent of the cell borders. With both cells now bordered,
        // the divider is gone — adjacent cells are separated by a 1-cell
        // horizontal gutter (a single space), and their right/left borders
        // form the visual separation.
        // The single bare `│` between cells produced a sequence like
        // "│{Reset}│" (right border of left cell, divider, left border of
        // right cell). After the redesign this no longer occurs because the
        // gutter is a space.
        var raw = RenderLauncherCapture(CreateBookmarks(4), selectedIndex: -1);
        raw.Should().NotContain("│[0m│",
            "the divider │ between adjacent boxes must be replaced by a single space");
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
    public async Task JumpToIndex_Digit3_With3Bookmarks_SelectsThirdBookmark()
    {
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 0;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.JumpToIndex, Count = 3, RawKeyChar = '3' },
            _options,
            CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(2);
    }

    [Fact]
    public async Task JumpToIndex_Digit5_With3Bookmarks_IsNoOp()
    {
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
    public async Task JumpToIndex_DoesNotJumpToReadingListTile()
    {
        // Reading List is the trailing tile (index == bookmarks.Count). Even
        // for a 9-bookmark list (digits 1-9 cover all bookmarks), the digit
        // badges only address real bookmarks; the Reading List is reached via
        // the dedicated `c` keybinding.
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 0;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.JumpToIndex, Count = 4, RawKeyChar = '4' },
            _options,
            CancellationToken.None);

        _navigationService.LauncherSelectedIndex.Should().Be(0,
            "digit 4 with 3 bookmarks must not select the Reading List tile (index 3)");
    }
}
