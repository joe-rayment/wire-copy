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
    /// Every row below is MEASURED, not derived: gates/probe-linktree-budget.mjs booted
    /// the real shell headful on 2026-07-26, read <c>#grid</c>'s height off the live
    /// pane docked and undocked at five window sizes, and divided by the card stride it
    /// also measured. The terminal dimensions are the pane's measured px converted at
    /// the calibrated 11.43 px/cell and 21.9 px/line. If this table stops matching,
    /// re-run the probe — do not adjust the expectations to fit the model.
    /// </remarks>
    [Theory]
    [InlineData(69, 41, 3)]     // 789x900 docked   — grid 667px / 177px stride
    [InlineData(76, 44, 4)]     // 877x982 docked   — grid 746px / 177px
    [InlineData(85, 45, 4)]     // 974x1000 docked  — grid 753px / 177px
    [InlineData(85, 59, 5)]     // 974x1300 docked  — grid 1053px / 177px
    [InlineData(55, 36, 3)]     // 638x800 docked   — grid 540px / 177px
    [InlineData(116, 41, 2)]    // 1330x900 undocked  — grid 600px / 227px stride
    [InlineData(129, 44, 2)]    // 1482x982 undocked  — grid 671px / 252px
    [InlineData(144, 45, 2)]    // 1650x1000 undocked — grid 689px / 254px
    [InlineData(144, 59, 3)]    // 1650x1300 undocked — grid 989px / 254px
    [InlineData(93, 36, 2)]     // 1070x800 undocked  — grid 542px / 186px
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

    [Theory]
    [InlineData(69, 41)]
    [InlineData(85, 59)]
    [InlineData(144, 45)]
    public void ShellPaneRows_FitThePanesRealPixels(int width, int height)
    {
        var rows = LinkTreeRenderer.ShellPaneRows(width, height);
        var stridePx = LinkTreeRenderer.ShellPaneCardStridePx(width * 11.43);
        var gridPx = ((height + 0.5) * 21.9) - LinkTreeRenderer.ShellPaneChromePx(width * 11.43);

        (rows * stridePx).Should().BeLessThanOrEqualTo(
            gridPx, "an admitted row the pane cannot draw is a link the user cannot see");
        ((rows + 1) * stridePx).Should().BeGreaterThan(
            gridPx, "leaving a whole further row unadmitted is the dead band the user reported");
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
