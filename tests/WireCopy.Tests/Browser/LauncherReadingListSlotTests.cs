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
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression tests for workspace-ul5z: the launcher Grid promotes Reading
/// List from a trailing tile to a reserved slot at virtual index 1, with a
/// secondary-accent background fill (ANSI 48;5;23 — dim cyan, design-system
/// counterpart to the AccentFg cyan reserved for interactive accents). The
/// slot is also protected from `d` (delete) and JumpToIndex now addresses it
/// as digit `2`.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class LauncherReadingListSlotTests
{
    private const int LargeTerminalWidth = 100;
    private const int TerminalHeight = 35;

    [Fact]
    public void Grid_RendersReadingListAtSlot1_WithSecondaryAccentBg()
    {
        // 3 bookmarks: virtual 0 = bookmark[0], virtual 1 = Reading List,
        // virtual 2 = bookmark[1], virtual 3 = bookmark[2]. The Reading List
        // cell carries the ANSI 48;5;23 background fill across all four box
        // rows so the whole tile reads as the secondary-accent surface.
        var raw = RenderLauncherCapture(CreateBookmarks(3), selectedIndex: -1);

        raw.Should().Contain("\x1b[48;5;23m",
            "the Reading List cell must use ANSI 48;5;23 as its secondary-accent background fill");

        var stripped = StripAnsi(raw);
        stripped.Should().Contain("READING LIST",
            "the Reading List title must render in the reserved slot");
    }

    [Fact]
    public void Grid_ReadingListBadge_IsDigitTwo_NotLetterC()
    {
        // The slot's badge follows the same digit-jump contract as bookmark
        // cells, so Reading List at virtual 1 advertises `[2]` (not the
        // legacy `[c]` letter badge).
        var raw = RenderLauncherCapture(CreateBookmarks(3), selectedIndex: -1);
        var stripped = StripAnsi(raw);

        stripped.Should().Contain("[2] │",
            "Reading List slot at virtual index 1 must advertise digit badge [2]");
        stripped.Should().NotContain("[c] │",
            "the legacy letter badge [c] must be removed from the bookmark grid");
    }

    [Fact]
    public void Grid_ReadingListBgReset_BeforeInterCellGutter()
    {
        // The cell must reset its background (\x1b[49m) before any subsequent
        // text — otherwise the inter-cell gutter or the next cell's content
        // will inherit the fill.
        var raw = RenderLauncherCapture(CreateBookmarks(3), selectedIndex: -1);

        raw.Should().Contain("\x1b[49m",
            "the Reading List cell must reset its background to default before inter-cell content");
    }

    [Fact]
    public void Grid_BookmarkOne_RendersAtSlot2()
    {
        // With Reading List at slot 1, bookmark[1] now renders at slot 2 with
        // badge `[3]`. We use distinct bookmark names to confirm the mapping.
        var bookmarks = new List<Bookmark>
        {
            Bookmark.Create("alpha", "https://alpha.example", 0),
            Bookmark.Create("bravo", "https://bravo.example", 1),
            Bookmark.Create("charlie", "https://charlie.example", 2),
        };

        var stripped = StripAnsi(RenderLauncherCapture(bookmarks, selectedIndex: -1));

        stripped.Should().Contain("ALPHA");
        stripped.Should().Contain("BRAVO");
        stripped.Should().Contain("CHARLIE");
        stripped.Should().Contain("[3] │",
            "bookmark[1] must render at slot 2 with badge [3]");
        stripped.Should().Contain("[4] │",
            "bookmark[2] must render at slot 3 with badge [4]");
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
/// Regression tests for workspace-ul5z dispatch: ActivateLink at the Reading
/// List slot opens collections, JumpToIndex addresses the slot, and DeleteItem
/// is a no-op against the protected slot.
/// </summary>
[Trait("Category", "Unit")]
public class LauncherReadingListDispatchTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly List<string> _navigatedUrls = new();
    private bool _refreshCollectionsCalled;

    public LauncherReadingListDispatchTests()
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

        var preloadService = Substitute.For<IPreloadService>();

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
            PreloadService = preloadService,
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (url, _, _) =>
            {
                _navigatedUrls.Add(url);
                return Task.CompletedTask;
            },
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
            RefreshCollectionsAsync = _ =>
            {
                _refreshCollectionsCalled = true;
                return Task.CompletedTask;
            },
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
    public async Task ActivateLink_AtSlot1_OpensCollections()
    {
        // Reading List slot lives at virtual index 1 (workspace-ul5z).
        // ActivateLink there must dispatch HandleOpenCollections — observable
        // by RefreshCollectionsAsync being invoked and the navigation service
        // entering the Collections view.
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 1;
        _refreshCollectionsCalled = false;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.ActivateLink },
            _options,
            CancellationToken.None);

        _refreshCollectionsCalled.Should().BeTrue(
            "selecting the Reading List slot and pressing Enter must enter the Collections view");
        _navigatedUrls.Should().BeEmpty(
            "the Reading List slot must NOT navigate to a bookmark URL");
    }

    [Fact]
    public async Task ActivateLink_AtSlot2_With3Bookmarks_NavigatesToBookmarkOne()
    {
        // With Reading List at slot 1, slot 2 maps to bookmark[1].
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 2;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.ActivateLink },
            _options,
            CancellationToken.None);

        _navigatedUrls.Should().ContainSingle()
            .Which.Should().Be("https://example.com/2",
                "slot 2 must address bookmark[1] (URL ending /2) under the Reading-List-at-slot-1 layout");
    }

    [Fact]
    public async Task ActivateLink_AtSlot0_NavigatesToBookmarkZero()
    {
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 0;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.ActivateLink },
            _options,
            CancellationToken.None);

        _navigatedUrls.Should().ContainSingle()
            .Which.Should().Be("https://example.com/1");
    }

    [Fact]
    public async Task DeleteItem_AtReadingListSlot_IsNoOp()
    {
        // The Reading List slot is protected from `d` — the user clears
        // saved articles via the collection screen, not from the launcher.
        _ctx.Bookmarks = CreateBookmarks(3);
        _navigationService.LauncherSelectedIndex = 1;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.DeleteItem },
            _options,
            CancellationToken.None);

        _ctx.Bookmarks!.Count.Should().Be(3,
            "no bookmark must be removed when DeleteItem fires on the Reading List slot");
        _ctx.PendingUndo.Should().BeNull(
            "the protected slot must not produce an undo entry");
    }

    [Fact]
    public async Task DeleteItem_AtSlot2_RemovesBookmarkOne()
    {
        // With Reading List at slot 1, slot 2 → bookmark[1] (the second
        // bookmark). Deleting slot 2 removes the second bookmark.
        var bookmarks = new List<Bookmark>
        {
            Bookmark.Create("alpha", "https://alpha.example", 0),
            Bookmark.Create("bravo", "https://bravo.example", 1),
            Bookmark.Create("charlie", "https://charlie.example", 2),
        };
        _ctx.Bookmarks = bookmarks;
        _navigationService.LauncherSelectedIndex = 2;

        await LauncherCommandHandler.Handle(
            _ctx,
            new NavigationCommand { Type = CommandType.DeleteItem },
            _options,
            CancellationToken.None);

        _ctx.Bookmarks!.Count.Should().Be(2);
        _ctx.Bookmarks.Should().NotContain(b => b.Name == "bravo",
            "slot 2 maps to bookmark[1] (bravo) under the Reading-List-at-slot-1 layout");
        _ctx.Bookmarks.Should().Contain(b => b.Name == "alpha");
        _ctx.Bookmarks.Should().Contain(b => b.Name == "charlie");
    }

    [Fact]
    public void IsReadingListSlot_VirtualIndex1_WithBookmarks_True()
    {
        LauncherCommandHandler.IsReadingListSlot(1, bookmarkCount: 3).Should().BeTrue();
    }

    [Fact]
    public void IsReadingListSlot_VirtualIndex1_WithoutBookmarks_False()
    {
        // When bookmarks.Count == 0 the empty-state screen takes over and the
        // Reading List slot has no virtual index.
        LauncherCommandHandler.IsReadingListSlot(1, bookmarkCount: 0).Should().BeFalse();
    }

    [Fact]
    public void IsReadingListSlot_OtherIndices_False()
    {
        LauncherCommandHandler.IsReadingListSlot(0, bookmarkCount: 3).Should().BeFalse();
        LauncherCommandHandler.IsReadingListSlot(2, bookmarkCount: 3).Should().BeFalse();
        LauncherCommandHandler.IsReadingListSlot(-1, bookmarkCount: 3).Should().BeFalse();
    }

    [Fact]
    public void BookmarkIndexFromVirtual_ZeroAndUp()
    {
        LauncherCommandHandler.BookmarkIndexFromVirtual(0).Should().Be(0);
        LauncherCommandHandler.BookmarkIndexFromVirtual(1).Should().Be(-1,
            "virtual 1 is the Reading List slot — no bookmark mapping");
        LauncherCommandHandler.BookmarkIndexFromVirtual(2).Should().Be(1);
        LauncherCommandHandler.BookmarkIndexFromVirtual(5).Should().Be(4);
    }
}
