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
/// workspace-vingi: the link-tree view-model exported over the shell channel
/// ("Link Tree Responsive.dc.html") comes from ONE builder
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
    [InlineData(69, 2)]    // ~780px — the docked-sidecar split at 1360px
    [InlineData(80, 2)]    // ~900px window
    [InlineData(121, 3)]   // ~1360px window
    [InlineData(149, 4)]   // ~1680px wide monitor
    [InlineData(300, 6)]   // absurd width clamps at six
    public void ShellPaneColumns_MatchesTheDesignBreakpoints(int terminalWidth, int expected)
    {
        LinkTreeRenderer.ShellPaneColumns(terminalWidth).Should().Be(expected);
    }

    [Fact]
    public void BuildViewState_ExportsTheAdmittedWindowWithSectionsStoriesAndSelection()
    {
        var page = CreatePage(sectionTwoStories: 2);
        var options = Options(terminalWidth: 121, terminalHeight: 40); // 3 pane columns

        var state = LinkTreeRenderer.BuildViewState(
            page, new NavigationContext(), options, maxLines: 60, linkCount: 6, sectionCount: 2);

        state.Columns.Should().Be(3);
        state.Title.Should().Be("Column Wire");
        state.Domain.Should().Be("127.0.0.1");
        state.LinkCount.Should().Be(6);
        state.SectionCount.Should().Be(2);

        // Window: header, 4 stories (3 + 1 rows), header, 2 stories — all admitted.
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
