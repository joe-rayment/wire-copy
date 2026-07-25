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
    [InlineData(55, 1)]    // ~620px pane — single column
    [InlineData(69, 1)]    // ~780px — the docked-sidecar split at 1360px
    [InlineData(82, 2)]    // ~920px — just past the 1 → 2 breakpoint
    [InlineData(121, 2)]   // ~1360px window
    [InlineData(135, 2)]   // ~1512px — the 15-inch MacBook, 2 columns at ~700px
    [InlineData(149, 2)]   // ~1680px — the FLUID track holds 2, it does not fan to 3
    [InlineData(176, 2)]   // ~1970px — still 2; the 700px track ceiling has to be paid for twice over
    [InlineData(207, 3)]   // ~2320px external display
    [InlineData(500, 6)]   // absurd width clamps at six
    public void ShellPaneColumns_MatchesTheDesignBreakpoints(int terminalWidth, int expected)
    {
        LinkTreeRenderer.ShellPaneColumns(terminalWidth).Should().Be(expected);
    }

    /// <summary>
    /// workspace-h2caq: "Link Tree.dc.html" cards run up to 242px, and the pane spends
    /// ~320px on its CRT masthead, rules and footer, so a shell-mode window budgeted in
    /// the terminal's own lines admits rows the pane then SLICES at its clipping edge.
    /// ComputeLayout has to hand back a budget that fits the pane's drawable height.
    /// The px expectations here are the same ones gate-linktree-html.mjs measures on the
    /// real pane (checks A11 / D…d).
    /// </summary>
    [Theory]
    [InlineData(70, 42, 3)]    // ~790x900 — the docked sidecar split, 165px cards
    [InlineData(121, 42, 2)]   // ~1360x900 undocked — 220px cards, only two rows fit
    [InlineData(150, 47, 2)]   // ~1680x1000 — 242px cards; a third row would be clipped
    [InlineData(207, 61, 3)]   // ~2320x1300 — tall enough to earn the third row back
    [InlineData(207, 33, 1)]   // wide but SHORT: big cards, almost no room
    public void ShellPaneBudget_AdmitsOnlyTheRowsThePaneCanDraw(int width, int height, int expectedRows)
    {
        var layout = LinkTreeRenderer.ComputeLayout(width, height, shellPane: true);

        // What the admission loop will actually spend, mirroring its own arithmetic:
        // a section header (3 lines) plus as many card rows as the budget allows.
        var budget = layout.PaneBudgetLines - 1; // the ▼-more line the loop reserves
        var rows = Math.Max(0, (budget - 3) / layout.CellHeight);
        rows.Should().Be(expectedRows);

        // …and those rows must fit the pane's real px, chrome paid for.
        var stridePx = LinkTreeRenderer.ShellPaneCardStridePx(width * 11.2);
        var gridPx = (height * 21.0) - LinkTreeRenderer.ShellPaneChromePx(width * 11.2);
        ((rows * stridePx) + 34).Should().BeLessThanOrEqualTo(gridPx,
            "the admitted rows plus a section header have to fit the grid the pane draws");
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
