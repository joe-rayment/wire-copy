// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-0jxo: the launcher view-model exported over the shell channel comes
/// from ONE builder (<see cref="LauncherRenderer.BuildViewState"/>) that the TUI
/// grid render and the JSON export both feed from, so the HTML launcher pane can
/// never drift from the terminal grid. These tests pin the exported view-model
/// (and its camelCase wire JSON) for the states the shell renders: default,
/// selection moved, URL typing, &gt;6 bookmarks paged, empty reading list, and
/// the setup hint.
/// </summary>
[Trait("Category", "Unit")]
public class LauncherViewStateTests
{
    // The channel serialises with these exact options (JsonSerializerDefaults.Web
    // ⇒ camelCase on the wire); mirror them so the assertions test the real
    // wire shape.
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Default_ThreeBookmarks_ComposesBookmarkAndReadingListSlots()
    {
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(3), selectedIndex: 0, Options());

        // total = 3 bookmarks + the Reading List virtual slot = 4, all on page 0.
        state.Paging.Should().Be(new LauncherPaging { Offset = 0, Visible = 4, Total = 4 });
        state.BookmarkCount.Should().Be(3);
        state.SelectedIndex.Should().Be(0, "the selected tile at virtual 0 maps to window index 0");
        state.Slots.Should().HaveCount(4);

        var first = state.Slots[0];
        first.Kind.Should().Be("bookmark");
        first.Title.Should().Be("BOOKMARK 1", "bookmark titles render upper-cased, exactly like the grid");
        first.Url.Should().Be("https://example.com/1");
        first.Hint.Should().Be("example.com", "the subtitle is the bookmark's domain");
        first.Badge.Should().Be("1");
        first.Star.Should().BeFalse();

        var readingList = state.Slots[1];
        readingList.Kind.Should().Be("readingList");
        readingList.Title.Should().Be("READING LIST");
        readingList.Url.Should().BeNull("the Reading List slot has no bookmark URL");
        readingList.Star.Should().BeTrue();
        readingList.Badge.Should().Be("2", "the Reading List slot is digit-jump address 2");

        state.Tagline.Should().Be("All copy, no nonsense");
        state.Version.Should().StartWith("v");
        state.Footer.Should().Be(new LauncherFooter { Badge = null, Status = null });
        state.SetupHint.Should().BeNull();
        state.UrlTyping.Should().BeNull();
    }

    [Fact]
    public void SelectionMoved_ToBookmarkTwo_MapsWindowSelectedIndex()
    {
        // Virtual slot 2 (bookmark[1]) selected — still page 0, so window index == virtual index.
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(3), selectedIndex: 2, Options());

        state.SelectedIndex.Should().Be(2);
        state.Slots[2].Kind.Should().Be("bookmark");
        state.Slots[2].Url.Should().Be("https://example.com/2", "virtual slot 2 addresses bookmark[1] under the Reading-List-at-1 layout");
        state.Slots[2].Badge.Should().Be("3");
    }

    [Fact]
    public void UrlTyping_UrlBarFocused_CarriesBufferAndCursor_AndSelectedIndexMinusOne()
    {
        var typing = new LauncherUrlTyping { Buffer = "git", Cursor = 3 };
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(3), selectedIndex: -1, Options(), typing);

        state.SelectedIndex.Should().Be(-1, "-1 signals the URL bar is focused");
        state.UrlTyping.Should().Be(typing);

        var json = JsonSerializer.Serialize(state, WireOptions);
        json.Should().Contain("\"urlTyping\":{\"buffer\":\"git\",\"cursor\":3}",
            "the typed buffer must reach the shell as camelCase so the HTML pane mirrors it");
    }

    [Fact]
    public void PagesToSelectedTilesPage_WithEmptyPadding()
    {
        // Options() is a SHORT window (35 lines): the pane's px budget fits 2 rows,
        // so a page is 2 columns × 2 rows = 4 tiles. 8 bookmarks + Reading List = 9
        // tiles → pages [0-3] [4-7] [8]. Selecting virtual slot 8 lands on the last
        // page, whose single real tile pads out to a whole 2-column row.
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(8), selectedIndex: 8, Options());

        state.Paging.Should().Be(new LauncherPaging { Offset = 8, Visible = 1, Total = 9 },
            "the last page shows the 1 remaining real tile out of 9 total");
        state.SelectedIndex.Should().Be(0, "virtual slot 8 is window index 8 - offset 8 = 0");
        state.Slots.Should().HaveCount(2, "the last page pads to a whole 2-column row");
        state.Slots[0].Index.Should().Be(8);
        state.Slots[1].Kind.Should().Be("empty", "the trailing grid cell past the last tile is an empty pad");
        state.Slots[1].Badge.Should().BeNull();
    }

    [Fact]
    public void EmptyReadingList_ShowsEmptyStateHint_AndStar()
    {
        var state = LauncherRenderer.BuildViewState(
            CreateBookmarks(2), selectedIndex: 0, Options(readingListCount: 0));

        var readingList = state.Slots[1];
        readingList.Kind.Should().Be("readingList");
        readingList.Star.Should().BeTrue();
        readingList.Hint.Should().Be("nothing saved yet — press s to add",
            "an empty Reading List shows the same empty-state subtitle the grid renders");
    }

    [Fact]
    public void PopulatedReadingList_ShowsSavedArticlesHint()
    {
        var state = LauncherRenderer.BuildViewState(
            CreateBookmarks(2), selectedIndex: 0, Options(readingListCount: 12));

        state.Slots[1].Hint.Should().Be("12 saved articles");
    }

    [Fact]
    public void SetupHint_WhenShown_ExportsTheHintCopy()
    {
        var options = Options() with { ShowSetupHint = true };
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(2), selectedIndex: 0, options);

        state.SetupHint.Should().Be("→ Optional: add API keys for podcasts & AI layout · press c");
    }

    [Fact]
    public void Footer_CarriesScheduledRunBadgeAndStatus()
    {
        var options = Options() with { ScheduledRunBadge = "1 scheduled run failed", StatusMessage = "Already at top" };
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(2), selectedIndex: 0, options);

        state.Footer.Badge.Should().Be("1 scheduled run failed");
        state.Footer.Status.Should().Be("Already at top");
    }

    [Fact]
    public void NoBookmarks_ExportsEmptyWindow()
    {
        var state = LauncherRenderer.BuildViewState(new List<Bookmark>(), selectedIndex: -1, Options());

        state.Slots.Should().BeEmpty("the empty-state screen has no tiles");
        state.Paging.Should().Be(new LauncherPaging { Offset = 0, Visible = 0, Total = 0 });
        state.BookmarkCount.Should().Be(0);
    }

    [Fact]
    public void WireJson_UsesCamelCaseFieldNames_AndNullUrlForReadingList()
    {
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(3), selectedIndex: 0, Options());
        var json = JsonSerializer.Serialize(state, WireOptions);

        json.Should().Contain("\"slots\":").And.Contain("\"selectedIndex\":");
        json.Should().Contain("\"paging\":{\"offset\":0,\"visible\":4,\"total\":4}");
        json.Should().Contain("\"kind\":\"readingList\"");
        json.Should().Contain("\"kind\":\"bookmark\"");
        json.Should().Contain("\"url\":null", "the Reading List slot serialises its URL as null");
        json.Should().Contain("\"star\":true");
    }

    // ── Pane grid geometry: ALWAYS 2 columns (the tile-grid contract) ─────────

    [Theory]
    [InlineData(60)]    // ~620px window
    [InlineData(100)]   // ~1000px
    [InlineData(130)]   // ~1280px
    [InlineData(171)]   // ~1680px — a 15" MacBook; the user rejected 3 columns here
    [InlineData(400)]   // absurd width
    public void ShellPaneColumns_IsAlwaysTwo(int width)
    {
        LauncherRenderer.ShellPaneColumns(width).Should().Be(
            2,
            "tile grids are always exactly 2 columns at every width — a width-dependent count is the regression");
    }

    [Fact]
    public void BuildViewState_WideWindow_StaysTwoColumnsAtEveryWidth()
    {
        // A wide (1280px-class) window is still 2 columns — the whole point of the
        // contract. Paging follows the pane's HEIGHT budget, not the width.
        var options = Options() with { TerminalWidth = 130 };

        var page0 = LauncherRenderer.BuildViewState(CreateBookmarks(11), selectedIndex: 0, options);
        page0.Columns.Should().Be(2);
        page0.Slots.Should().HaveCount(4, "35 lines fits 2 rows → a 2 × 2 page");
        page0.Paging.Should().Be(new LauncherPaging { Offset = 0, Visible = 4, Total = 12 });
    }

    [Theory]
    [InlineData(35, 2)]    // short window — 2 rows fit under the pane's chrome
    [InlineData(46, 4)]    // ~1000px tall: the user's 2 × 4 = 8 tiles fill the pane
    [InlineData(60, 6)]    // a tall external display
    public void PaneRowsPerPage_GrowsWithWindowHeight(int terminalHeight, int expectedRows)
    {
        // The pane's rows follow its OWN px budget, so the grid fills the pane's
        // height instead of stopping at a hard-coded three rows. Mirroring the
        // terminal's row count would under-fill: its card is a quarter of the
        // screen tall, the pane's is a roughly constant 122px.
        var options = Options() with { TerminalWidth = 152, TerminalHeight = terminalHeight };

        LauncherRenderer.PaneRowsPerPage(options).Should().Be(expectedRows);
    }

    private static RenderOptions Options(int? readingListCount = null) => new()
    {
        TerminalWidth = 100,
        TerminalHeight = 35,
        LayoutVariant = "Grid",
        ReadingListItemCount = readingListCount,
    };

    private static List<Bookmark> CreateBookmarks(int count)
    {
        var list = new List<Bookmark>();
        for (var i = 0; i < count; i++)
        {
            list.Add(Bookmark.Create($"bookmark {i + 1}", $"https://example.com/{i + 1}", i));
        }

        return list;
    }
}
