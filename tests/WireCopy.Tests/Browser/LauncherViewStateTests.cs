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
    public void MoreThanSixBookmarks_PagesToSelectedTilesPage_WithEmptyPadding()
    {
        // 8 bookmarks + Reading List = 9 tiles. Selecting virtual slot 7 lands on page 1 (offset 6).
        var state = LauncherRenderer.BuildViewState(CreateBookmarks(8), selectedIndex: 7, Options());

        state.Paging.Should().Be(new LauncherPaging { Offset = 6, Visible = 3, Total = 9 },
            "page 1 shows the 3 remaining real tiles out of 9 total");
        state.SelectedIndex.Should().Be(1, "virtual slot 7 is window index 7 - offset 6 = 1");
        state.Slots.Should().HaveCount(4, "the last page pads to a whole 2-column row");
        state.Slots[0].Index.Should().Be(6);
        state.Slots[2].Index.Should().Be(8);
        state.Slots[3].Kind.Should().Be("empty", "the trailing grid cell past the last tile is an empty pad");
        state.Slots[3].Badge.Should().BeNull();
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

    // ── Responsive pane geometry ("Launcher Responsive.dc.html") ──────────────

    [Theory]
    [InlineData(60, 1)]    // ~620px window — single column
    [InlineData(100, 2)]   // ~1000px — two columns (the legacy 2×3 shape)
    [InlineData(130, 3)]   // ~1280px — three columns
    [InlineData(171, 4)]   // ~1680px — four columns
    [InlineData(400, 5)]   // absurd width clamps at five
    public void ShellPaneColumns_FlowsWithTerminalWidth(int width, int expectedColumns)
    {
        LauncherRenderer.ShellPaneColumns(width).Should().Be(expectedColumns);
    }

    [Fact]
    public void BuildViewState_WideWindow_ExportsColumnsAndPagesByColumnsTimesThreeRows()
    {
        // 130 cells → 3 columns → 9-tile pages. 11 bookmarks + Reading List = 12
        // total: page 0 carries 9 tiles, and selecting virtual 10 lands on page 1
        // with the remaining 3 tiles (padded to a full 3-column row exactly).
        var options = Options() with { TerminalWidth = 130 };

        var page0 = LauncherRenderer.BuildViewState(CreateBookmarks(11), selectedIndex: 0, options);
        page0.Columns.Should().Be(3);
        page0.Slots.Should().HaveCount(9);
        page0.Paging.Should().Be(new LauncherPaging { Offset = 0, Visible = 9, Total = 12 });

        var page1 = LauncherRenderer.BuildViewState(CreateBookmarks(11), selectedIndex: 10, options);
        page1.Columns.Should().Be(3);
        page1.Paging.Should().Be(new LauncherPaging { Offset = 9, Visible = 3, Total = 12 });
        page1.Slots.Should().HaveCount(3, "3 remaining tiles already fill one 3-column row — no pads");
        page1.SelectedIndex.Should().Be(1, "virtual 10 maps into the page-1 window at 10 − 9");
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
