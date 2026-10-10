// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-vingi / workspace-h2caq: the link-tree view-model exported over the
/// shell channel ("Link Tree.dc.html") comes from ONE builder
/// (<see cref="LinkTreeRenderer.BuildViewState"/>) that mirrors the grid
/// render's admission loop, so the HTML link-tree pane can never drift from the
/// terminal grid. These tests pin the responsive column formula and the
/// exported window/selection/counters.
/// </summary>
[Trait("Category", "Unit")]
public class LinkTreeViewStateTests
{
    [Theory]
    [InlineData(55)]    // ~630px pane
    [InlineData(69)]    // ~790px — the docked-sidecar split at 1360px, which used to collapse to 1
    [InlineData(121)]   // ~1380px window
    [InlineData(135)]   // ~1540px — the 15-inch MacBook
    [InlineData(149)]   // ~1700px
    [InlineData(207)]   // ~2370px external display, which used to fan out to 3
    [InlineData(500)]   // absurd width
    public void ShellPaneColumns_IsAlwaysTwo(int terminalWidth)
    {
        LinkTreeRenderer.ShellPaneColumns(terminalWidth).Should().Be(
            2,
            "tile grids are always exactly 2 columns — the docked collapse to 1 and the fan-out to 3 "
            + "are the two extremes ResponsiveGrid's remarks already record as rejected");
    }

    /// <summary>
    /// The admitted row count must equal the number of card rows the REAL pane draws —
    /// too few and links vanish under a dead band (the user's 2026-07-26 report: "3 of
    /// 15 shown" with the bottom of the pane empty); too many and the grid slices a card
    /// at its clipping edge.
    /// </summary>
    /// <remarks>
    /// The grid heights below are MEASURED, not derived: gates/probe-linktree-budget.mjs
    /// booted the real shell headful on 2026-07-26 and read <c>#grid</c>'s height off the
    /// live pane docked and undocked at five window sizes. The terminal dimensions are
    /// that measured px converted at the calibrated 11.43 px/cell and 21.9 px/line. If
    /// the geometry stops matching, re-run the probe — do not adjust the expectations to
    /// fit the model.
    ///
    /// The COUNTS changed with workspace-wp16o and the comments record why: the rows now
    /// share the grid's height instead of each taking exactly one stride, so five of
    /// these sizes hold a row more than <c>floor(gridPx / stride)</c> allowed, and the
    /// height each row ends up with is what the next test pins.
    /// </remarks>
    [Theory]
    [InlineData(69, 41, 3)]     // 789x900 docked    — grid 676px, 3 rows of 225px  (floor said 3)
    [InlineData(76, 44, 4)]     // 877x982 docked    — grid 737px, 4 rows of 184px  (floor said 4)
    [InlineData(85, 45, 4)]     // 974x1000 docked   — grid 748px, 4 rows of 187px  (floor said 4)
    [InlineData(85, 59, 5)]     // 974x1300 docked   — grid 1054px, 5 rows of 211px (floor said 5)
    [InlineData(55, 36, 3)]     // 638x800 docked    — grid 566px, 3 rows of 189px  (floor said 3)
    [InlineData(116, 41, 3)]    // 1330x900 undocked — grid 622px, 3 rows of 207px  (floor said 2)
    [InlineData(129, 44, 3)]    // 1482x982 undocked — grid 671px, 3 rows of 224px  (floor said 2)
    [InlineData(144, 45, 3)]    // 1650x1000 undocked — grid 683px, 3 rows of 228px (floor said 2)
    [InlineData(144, 59, 4)]    // 1650x1300 undocked — grid 990px, 4 rows of 248px (floor said 3)
    [InlineData(93, 36, 2 + 1)] // 1070x800 undocked — grid 541px, 3 rows of 180px  (floor said 2)
    public void ShellPaneRows_MatchesWhatTheRealPaneDraws(int width, int height, int expectedRows)
    {
        LinkTreeRenderer.ShellPaneRows(width, height).Should().Be(expectedRows);

        // The admission loop must be able to SPEND those rows: its budget is
        // min(VisibleRows × CellHeight, PaneBudgetLines), so both have to clear
        // expectedRows × CellHeight or a row the pane can draw goes unadmitted.
        var layout = LinkTreeRenderer.ComputeLayout(width, height, shellPane: true);
        var needed = expectedRows * layout.CellHeight;
        layout.PaneBudgetLines.Should().BeGreaterThanOrEqualTo(
            needed, "the budget has to cover every row the pane can draw");
        (layout.VisibleRows * layout.CellHeight).Should().BeGreaterThanOrEqualTo(
            needed, "VisibleRows must not claw back what PaneBudgetLines granted");

        // …and it must not cover a row the pane CANNOT draw.
        layout.PaneBudgetLines.Should().BeLessThan(
            (expectedRows + 1) * layout.CellHeight,
            "admitting a row the pane cannot draw slices a card at the clipping edge");
    }

    /// <summary>
    /// workspace-wp16o: the admitted rows SHARE the grid's height, so there is no
    /// remainder to leave behind — and the height each row lands on has to stay inside
    /// the design's own card bounds, or the fix trades a dead band for a stretched card.
    /// </summary>
    [Theory]
    [InlineData(69, 41)]
    [InlineData(85, 59)]
    [InlineData(144, 45)]
    [InlineData(144, 59)]
    [InlineData(93, 36)]
    [InlineData(55, 36)]
    public void ShellPaneRows_ShareTheGridWithNothingLeftOver(int width, int height)
    {
        var rows = LinkTreeRenderer.ShellPaneRows(width, height);
        var gridPx = ((height + 0.5) * 21.9) - LinkTreeRenderer.ShellPaneChromePx(width * 11.43);
        var rowPx = gridPx / rows;

        rowPx.Should().BeInRange(
            LinkTreeRenderer.ShellPaneMinRowPx, LinkTreeRenderer.ShellPaneMaxRowPx,
            "a row shorter than the design's 165px card is squashed and one taller than "
            + "its 242px card is inflated — the band must not become a stretch");

        // The whole grid is spent: no further row would fit, and none is left half-empty.
        (rows * rowPx).Should().BeApproximately(gridPx, 0.001,
            "1fr tracks divide the grid exactly — the dead band is arithmetically gone");
    }

    /// <summary>
    /// Across every window the user can make, the pane must never waste enough height to
    /// have drawn another row. Sweeping is the point: the bug this replaces fired at
    /// 10.6% of window sizes (workspace-wm1kd) and nothing in the suite swept heights.
    /// </summary>
    /// <remarks>
    /// The rows are never SHORTER than the design's card, which the row walk guarantees
    /// outright. They can be marginally taller in one narrow regime — when the grid is
    /// too tall for N rows and too short for N+1, which only happens at one or two rows,
    /// where the bounds sit further apart than the grid itself. linktree.css caps the
    /// card at 242px there, so the overshoot shows as a band; what must hold is that the
    /// band never adds up to a row the pane could have drawn instead.
    /// </remarks>
    [Fact]
    public void ShellPaneRows_NeverWasteAFurtherRowAcrossTheWholeSweep()
    {
        var squashed = new List<string>();
        var wasted = new List<string>();
        for (var w = 50; w <= 200; w++)
        {
            for (var h = 30; h <= 70; h++)
            {
                var gridPx = ((h + 0.5) * 21.9) - LinkTreeRenderer.ShellPaneChromePx(w * 11.43);
                if (gridPx < LinkTreeRenderer.ShellPaneMinRowPx)
                {
                    continue; // too short for even one row; the pane scrolls instead
                }

                var rows = LinkTreeRenderer.ShellPaneRows(w, h);
                var rowPx = gridPx / rows;
                if (rowPx < LinkTreeRenderer.ShellPaneMinRowPx - 0.001)
                {
                    squashed.Add($"{w}x{h} → {rowPx:F0}px rows");
                }

                var band = rows * Math.Max(0, rowPx - LinkTreeRenderer.ShellPaneMaxRowPx);
                if (band >= LinkTreeRenderer.ShellPaneMinRowPx)
                {
                    wasted.Add($"{w}x{h} → {band:F0}px unused over {rows} rows");
                }
            }
        }

        squashed.Should().BeEmpty("a row shorter than the design's 165px card is a crushed card");
        wasted.Should().BeEmpty(
            "wasting a whole row's worth of height IS the dead band this replaced");
    }

    [Fact]
    public void BuildViewState_ExportsTheAdmittedWindowWithSectionsStoriesAndSelection()
    {
        var page = CreatePage(sectionTwoStories: 2);
        // 2 pane columns; tall enough that the pane's own drawable height
        // (ShellPaneGridLines) is not the binding constraint on the window.
        var options = Options(terminalWidth: 121, terminalHeight: 60);

        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 60, linkCount: 6, sectionCount: 2);

        state.Columns.Should().Be(2);
        state.Title.Should().Be("Column Wire");
        state.Domain.Should().Be("127.0.0.1");
        state.LinkCount.Should().Be(6);
        state.SectionCount.Should().Be(2);

        // Window: header, 4 stories (2 + 2 rows), header, 2 stories — all admitted.
        state.Items.Select(i => i.Kind).Should().ContainInOrder(
            "section", "story", "story", "story", "story", "section", "story", "story");
        state.Items[0].Title.Should().Be("Dispatches");
        state.Items[0].Count.Should().Be(4);
        state.Items[0].Collapsed.Should().BeFalse();

        var story = state.Items[1];
        story.Title.Should().Be("Story 1");
        story.Url.Should().Be("https://example.com/story-1");
        story.Meta.Should().Be("Ada Lovelace · Jul 19");
        story.LinkType.Should().Be("content");

        // The tree auto-selects its first child (the Dispatches header) = item 0.
        state.SelectedIndex.Should().Be(0);
        state.ShownStories.Should().Be(6);
        state.TotalStories.Should().Be(6);
        state.MoreAbove.Should().Be(0);
        state.MoreBelow.Should().Be(0);
        state.Docked.Should().BeFalse();
    }

    [Fact]
    public void BuildViewState_TightLineBudget_WindowsTheRowsAndCountsTheRest()
    {
        var page = CreatePage(sectionTwoStories: 2);
        var options = Options(terminalWidth: 121, terminalHeight: 40);

        // A tight budget must admit only a PREFIX of the 5 grid rows
        // ([h1][s1-s3][s4][h2][o1-o2]) and report the rest as MoreBelow —
        // exactly like the render loop's own admission.
        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 14, linkCount: 6, sectionCount: 2);

        state.Items.Count.Should().BeLessThan(8, "the full tree cannot fit in 14 lines");
        state.Items[0].Kind.Should().Be("section", "the window starts at the top row");
        state.MoreAbove.Should().Be(0);
        state.MoreBelow.Should().BeGreaterThan(0, "unadmitted rows must be reported below the window");
        state.ShownStories.Should().Be(state.Items.Count(i => i.Kind == "story"));
    }

    /// <summary>
    /// workspace-u4p2z: the pager's "▼ N more below" is rendered beside "N of M shown"
    /// and a N/M footer, both of which count STORIES — so N has to as well. It counted
    /// grid ROWS, which put "6 of 15 shown" next to "5 more below" when 9 were below.
    /// The arithmetic that must hold is shown + below == total, with no group header
    /// ever counted as a story.
    /// </summary>
    [Fact]
    public void BuildViewState_MoreBelowCountsSTORIESNotGridRows()
    {
        var page = CreatePage(sectionTwoStories: 2);
        var options = Options(terminalWidth: 121, terminalHeight: 40);

        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 16, linkCount: 6, sectionCount: 2);

        state.ShownStories.Should().BeGreaterThan(0).And.BeLessThan(state.TotalStories,
            "this budget must window the tree — otherwise the check proves nothing");
        (state.ShownStories + state.MoreBelow).Should().Be(
            state.TotalStories,
            "with nothing scrolled above, what is shown plus what is below IS the total — "
            + "the number beside '6 of 15 shown' has to be 9, not the 5 grid rows holding them");
        state.MoreAbove.Should().Be(0);
    }

    /// <summary>
    /// The same identity with the window scrolled: above + shown + below == total.
    /// A row-counting pager cannot satisfy this at 2 columns.
    /// </summary>
    [Fact]
    public void BuildViewState_ScrolledWindow_AboveShownAndBelowAreStoriesThatSumToTheTotal()
    {
        var page = CreatePage(sectionTwoStories: 2);
        var options = Options(terminalWidth: 121, terminalHeight: 40);
        var context = new NavigationContext { ScrollOffset = 2 };

        var state = LinkTreeRenderer.BuildViewState(
            page, context, options, maxLines: 16, linkCount: 6, sectionCount: 2);

        state.MoreAbove.Should().BeGreaterThan(0, "the window starts below the first grid row");
        (state.MoreAbove + state.ShownStories + state.MoreBelow).Should().Be(
            state.TotalStories,
            "every number the pane's chrome shows is stories, so they must account for all of them");
    }

    /// <summary>
    /// workspace-wm1kd: the admission loop reserved one line for the ▼ scroll indicator,
    /// and whenever the pane's line budget came out an exact multiple of the cell height
    /// that single line cost a WHOLE card row — a ~20px remainder became a 180–250px
    /// blank band. A sweep of 3663 window sizes put it at 10.6%, firing at terminal
    /// height 35, 43, 51 and 59 for EVERY width. In the pane that reserve is wrong
    /// outright: the ▼ mark is drawn in the pane's footer, outside #grid, and is already
    /// inside the measured chrome.
    /// </summary>
    [Theory]
    [InlineData(72, 35)]
    [InlineData(72, 43)]
    [InlineData(72, 51)]
    [InlineData(72, 59)]
    [InlineData(100, 43)]
    [InlineData(144, 51)]
    public void BuildViewState_AdmitsEveryRowThePaneCanDraw_EvenWithMoreBelow(int width, int height)
    {
        // A flat list of stories: no section headers, so the ONLY thing that can cost a
        // row is the reserve this pins. Far more stories than fit, so "more below" is
        // always true and the reserve is always in play.
        var page = CreateFlatPage(60);
        var options = Options(width, height);

        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 500, linkCount: 60, sectionCount: 0);

        var expected = LinkTreeRenderer.ShellPaneRows(width, height);
        var rendered = (int)Math.Ceiling(state.Items.Count(i => i.Kind == "story") / (double)state.Columns);

        state.MoreBelow.Should().BeGreaterThan(0, "the fixture must overflow or the reserve never applies");
        rendered.Should().Be(
            expected,
            "the pane draws ShellPaneRows rows, so the loop must admit exactly that many — "
            + "one short is a whole card row of blank space");
    }

    /// <summary>
    /// The other half of workspace-wm1kd: when the TERMINAL's own line budget is what
    /// binds — and it lands on an exact multiple of the cell height — the ▼ reserve must
    /// still not cost a card row. In the pane that reserve buys nothing: the ▼ mark is
    /// drawn in the pane's footer, outside #grid, and is already inside the measured
    /// chrome, so it is not charged in shell mode at all.
    /// </summary>
    [Theory]
    [InlineData(72, 43)]
    [InlineData(100, 51)]
    [InlineData(144, 45)]
    public void BuildViewState_ExactLineBudget_StillAdmitsEveryRow(int width, int height)
    {
        var page = CreateFlatPage(60);
        var options = Options(width, height);
        var layout = LinkTreeRenderer.ComputeLayout(width, height, shellPane: true);
        var rows = LinkTreeRenderer.ShellPaneRows(width, height);

        // Exactly enough lines for the rows and not one more — the shape that used to
        // lose a whole row to the reserve.
        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options,
            maxLines: rows * layout.CellHeight, linkCount: 60, sectionCount: 0);

        var rendered = (int)Math.Ceiling(state.Items.Count(i => i.Kind == "story") / (double)state.Columns);
        state.MoreBelow.Should().BeGreaterThan(0, "the fixture must overflow or the reserve never applies");
        rendered.Should().Be(rows, "a budget of exactly N rows has to admit N rows");
    }

    // Guards:   the link-tree header's "cached" tag (LinkTreeViewState.Cached) shows for a page that was
    //           reached through a redirect: the page cache is keyed by the URL the user ASKED for, but
    //           page.Url is the FINAL url after the redirect.
    // Fails if: BuildViewState looks only at page.Url (the old behavior), so a revisit of a redirected page
    //           loses its "cached" tag; or it ignores page.RequestedUrl / the URL normalizer.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~BuildViewState_CachedTag"
    [Theory]
    [InlineData("http://127.0.0.1:8163/world", "http://127.0.0.1:8163/world")]       // exact request url
    [InlineData("http://127.0.0.1:8163/world/", "http://127.0.0.1:8163/world")]      // trailing slash: normalized key
    [InlineData("HTTP://127.0.0.1:8163/world#top", "http://127.0.0.1:8163/world")]   // case + fragment: normalized key
    public void BuildViewState_CachedTag_ShowsForARedirectedPageWhoseRequestUrlIsCached(string requestedUrl, string cachedEntry)
    {
        // page.Url is the FINAL url ("http://127.0.0.1:8163/"); only the REQUEST url is in the cache.
        var page = CreatePage(sectionTwoStories: 2);
        page.RequestedUrl = requestedUrl;
        var options = Options(terminalWidth: 121, terminalHeight: 60);
        options = options with { CachedUrls = new HashSet<string> { cachedEntry } };

        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 60, linkCount: 6, sectionCount: 2);

        state.Cached.Should().BeTrue("the page cache holds this page under the URL the user requested");
    }

    [Fact]
    public void BuildViewState_CachedTag_StaysOffWhenNeitherTheRequestNorTheFinalUrlIsCached()
    {
        var page = CreatePage(sectionTwoStories: 2);
        page.RequestedUrl = "http://127.0.0.1:8163/world";
        var options = Options(terminalWidth: 121, terminalHeight: 60);
        options = options with { CachedUrls = new HashSet<string> { "http://127.0.0.1:8163/somewhere-else" } };

        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 60, linkCount: 6, sectionCount: 2);

        state.Cached.Should().BeFalse();
    }

    [Fact]
    public void BuildViewState_CachedTag_StillWorksForAPageWithoutARequestedUrlWhoseFinalUrlIsCached()
    {
        var page = CreatePage(sectionTwoStories: 2); // RequestedUrl never set (null)
        var options = Options(terminalWidth: 121, terminalHeight: 60);
        options = options with { CachedUrls = new HashSet<string> { page.Url } };

        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 60, linkCount: 6, sectionCount: 2);

        state.Cached.Should().BeTrue();
    }

    private static Page CreateFlatPage(int stories)
    {
        var root = LinkNode.CreateRoot();
        for (var i = 1; i <= stories; i++)
        {
            root.AddChild(Story($"Story {i}"));
        }

        var page = Page.Create(
            "http://127.0.0.1:8163/",
            "<html></html>",
            new PageMetadata { Title = "Flat Wire" });
        page.SetLinkTree(NavigationTree.BuildFromRoot(root));
        return page;
    }

    private static RenderOptions Options(int terminalWidth, int terminalHeight) => new()
    {
        TerminalWidth = terminalWidth,
        TerminalHeight = terminalHeight,
        ShellPane = true,
    };

    private static Page CreatePage(int sectionTwoStories)
    {
        var root = LinkNode.CreateRoot();
        var header1 = root.AddChild(GroupHeader("Dispatches"));
        for (var i = 1; i <= 4; i++)
        {
            header1.AddChild(Story($"Story {i}"));
        }

        var header2 = root.AddChild(GroupHeader("Archive"));
        for (var i = 1; i <= sectionTwoStories; i++)
        {
            header2.AddChild(Story($"Old {i}"));
        }

        var page = Page.Create(
            "http://127.0.0.1:8163/",
            "<html></html>",
            new PageMetadata { Title = "Column Wire" });
        page.SetLinkTree(NavigationTree.BuildFromRoot(root));
        return page;
    }

    private static LinkInfo Story(string title) => new()
    {
        DisplayText = title,
        Url = $"https://example.com/{title.ToLowerInvariant().Replace(' ', '-')}",
        Type = LinkType.Content,
        ImportanceScore = 50,
        Author = "Ada Lovelace",
        PublishedDate = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc),
    };

    private static LinkInfo GroupHeader(string name) => new()
    {
        DisplayText = name,
        Url = string.Empty,
        Type = LinkType.Content,
        ImportanceScore = 100,
        HeaderType = HeaderType.TopLevelGroup,
    };
}
