// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkTreeGridMapperTests
{
    #region MapToGrid

    [Fact]
    public void MapToGrid_SingleColumn_EachNodeGetsOwnRow()
    {
        var nodes = CreateFlatNodes(5);

        var grid = LinkTreeGridMapper.MapToGrid(nodes, 1);

        grid.Should().HaveCount(5);
        foreach (var row in grid)
        {
            row.Right.Should().BeNull();
        }
    }

    [Fact]
    public void MapToGrid_TwoColumns_PairsRegularLinks()
    {
        var nodes = CreateFlatNodes(4);

        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        grid.Should().HaveCount(2);
        grid[0].Left.Should().NotBeNull();
        grid[0].Right.Should().NotBeNull();
        grid[1].Left.Should().NotBeNull();
        grid[1].Right.Should().NotBeNull();
    }

    [Fact]
    public void MapToGrid_OddNumberOfLinks_LastRowHasNullRight()
    {
        var nodes = CreateFlatNodes(3);

        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        grid.Should().HaveCount(2);
        grid[0].Right.Should().NotBeNull();
        grid[1].Right.Should().BeNull();
    }

    [Fact]
    public void MapToGrid_GroupHeadersGetFullWidthRow()
    {
        var root = LinkNode.CreateRoot();
        var header = root.AddChild(new LinkInfo
        {
            DisplayText = "Content",
            Url = string.Empty,
            Type = LinkType.Content,
            ImportanceScore = 100,
            HeaderType = HeaderType.TopLevelGroup
        });
        var link1 = root.AddChild(CreateLinkInfo("Article 1"));
        var link2 = root.AddChild(CreateLinkInfo("Article 2"));

        var nodes = new List<LinkNode> { header, link1, link2 };
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        grid.Should().HaveCount(2);
        grid[0].IsGroupHeader.Should().BeTrue();
        grid[0].Right.Should().BeNull();
        grid[1].IsGroupHeader.Should().BeFalse();
        grid[1].Right.Should().NotBeNull();
    }

    [Fact]
    public void MapToGrid_GroupHeadersSeparateSections()
    {
        var root = LinkNode.CreateRoot();
        var header1 = root.AddChild(CreateGroupHeaderInfo("Content"));
        var link1 = root.AddChild(CreateLinkInfo("Article 1"));
        var header2 = root.AddChild(CreateGroupHeaderInfo("Navigation"));
        var link2 = root.AddChild(CreateLinkInfo("Home"));
        var link3 = root.AddChild(CreateLinkInfo("About"));

        var nodes = new List<LinkNode> { header1, link1, header2, link2, link3 };
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        // header1 (full row), link1 (alone), header2 (full row), link2+link3 (paired)
        grid.Should().HaveCount(4);
        grid[0].IsGroupHeader.Should().BeTrue();
        grid[1].Right.Should().BeNull(); // link1 alone before next header
        grid[2].IsGroupHeader.Should().BeTrue();
        grid[3].Right.Should().NotBeNull(); // link2 + link3 paired
    }

    #endregion

    #region NodeIndexToGridPosition and GridPositionToNodeIndex round-trip

    [Fact]
    public void NodeIndexToGridPosition_ReturnsCorrectPosition()
    {
        var nodes = CreateFlatNodes(4);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        LinkTreeGridMapper.NodeIndexToGridPosition(grid, 0).Should().Be((0, 0));
        LinkTreeGridMapper.NodeIndexToGridPosition(grid, 1).Should().Be((0, 1));
        LinkTreeGridMapper.NodeIndexToGridPosition(grid, 2).Should().Be((1, 0));
        LinkTreeGridMapper.NodeIndexToGridPosition(grid, 3).Should().Be((1, 1));
    }

    [Fact]
    public void GridPositionToNodeIndex_ReturnsCorrectIndex()
    {
        var nodes = CreateFlatNodes(4);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        LinkTreeGridMapper.GridPositionToNodeIndex(grid, 0, 0).Should().Be(0);
        LinkTreeGridMapper.GridPositionToNodeIndex(grid, 0, 1).Should().Be(1);
        LinkTreeGridMapper.GridPositionToNodeIndex(grid, 1, 0).Should().Be(2);
        LinkTreeGridMapper.GridPositionToNodeIndex(grid, 1, 1).Should().Be(3);
    }

    [Fact]
    public void NodeIndex_RoundTrips_ThroughGridPosition()
    {
        var nodes = CreateFlatNodes(6);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        for (var i = 0; i < nodes.Count; i++)
        {
            var (row, col) = LinkTreeGridMapper.NodeIndexToGridPosition(grid, i);
            var roundTripped = LinkTreeGridMapper.GridPositionToNodeIndex(grid, row, col);
            roundTripped.Should().Be(i, because: $"node index {i} should round-trip through grid position ({row},{col})");
        }
    }

    #endregion

    #region Column-preserving movement

    [Fact]
    public void MoveDown_PreservesColumn()
    {
        var nodes = CreateFlatNodes(4);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        // Starting at (0, 1) = node 1, move down should go to (1, 1) = node 3
        var result = LinkTreeGridMapper.MoveDown(grid, 0, 1);
        result.Should().Be(3);
    }

    [Fact]
    public void MoveUp_PreservesColumn()
    {
        var nodes = CreateFlatNodes(4);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        // Starting at (1, 1) = node 3, move up should go to (0, 1) = node 1
        var result = LinkTreeGridMapper.MoveUp(grid, 1, 1);
        result.Should().Be(1);
    }

    [Fact]
    public void MoveDown_FallsBackToLeftWhenNoRight()
    {
        var nodes = CreateFlatNodes(3); // 2 columns: [0,1] [2,null]
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        // From (0, 1), move down. Row 1 has no right, should fall back to left (node 2)
        var result = LinkTreeGridMapper.MoveDown(grid, 0, 1);
        result.Should().Be(2);
    }

    [Fact]
    public void MoveDown_AcrossGroupHeader()
    {
        var root = LinkNode.CreateRoot();
        var link1 = root.AddChild(CreateLinkInfo("Article 1"));
        var link2 = root.AddChild(CreateLinkInfo("Article 2"));
        var header = root.AddChild(CreateGroupHeaderInfo("Navigation"));
        var link3 = root.AddChild(CreateLinkInfo("Home"));

        var nodes = new List<LinkNode> { link1, link2, header, link3 };
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        // Row 0: link1+link2, Row 1: header, Row 2: link3
        // MoveDown from row 0 should land on row 1 (header)
        var result = LinkTreeGridMapper.MoveDown(grid, 0, 0);
        result.Should().Be(2); // header is at node index 2
    }

    [Fact]
    public void MoveDown_AtBottom_StaysInPlace()
    {
        var nodes = CreateFlatNodes(2);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        // Only 1 row — moving down stays on row 0
        var result = LinkTreeGridMapper.MoveDown(grid, 0, 0);
        result.Should().Be(0);
    }

    [Fact]
    public void MoveUp_AtTop_StaysInPlace()
    {
        var nodes = CreateFlatNodes(4);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        var result = LinkTreeGridMapper.MoveUp(grid, 0, 1);
        result.Should().Be(1);
    }

    #endregion

    #region Edge cases

    [Fact]
    public void MapToGrid_SingleLink_OneRowNoRight()
    {
        var nodes = CreateFlatNodes(1);
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        grid.Should().HaveCount(1);
        grid[0].Right.Should().BeNull();
    }

    [Fact]
    public void MapToGrid_AllGroupHeaders_AllFullWidth()
    {
        var root = LinkNode.CreateRoot();
        var h1 = root.AddChild(CreateGroupHeaderInfo("Content"));
        var h2 = root.AddChild(CreateGroupHeaderInfo("Navigation"));

        var nodes = new List<LinkNode> { h1, h2 };
        var grid = LinkTreeGridMapper.MapToGrid(nodes, 2);

        grid.Should().HaveCount(2);
        grid.Should().AllSatisfy(r => r.IsGroupHeader.Should().BeTrue());
        grid.Should().AllSatisfy(r => r.Right.Should().BeNull());
    }

    [Fact]
    public void MapToGrid_EmptyList_ReturnsEmpty()
    {
        var grid = LinkTreeGridMapper.MapToGrid(new List<LinkNode>(), 2);
        grid.Should().BeEmpty();
    }

    #endregion

    private static List<LinkNode> CreateFlatNodes(int count)
    {
        var root = LinkNode.CreateRoot();
        var nodes = new List<LinkNode>();
        for (var i = 0; i < count; i++)
        {
            nodes.Add(root.AddChild(CreateLinkInfo($"Link {i + 1}")));
        }

        return nodes;
    }

    private static LinkInfo CreateLinkInfo(string title)
    {
        return new LinkInfo
        {
            DisplayText = title,
            Url = $"https://example.com/{title.ToLowerInvariant().Replace(' ', '-')}",
            Type = LinkType.Content,
            ImportanceScore = 50
        };
    }

    private static LinkInfo CreateGroupHeaderInfo(string name)
    {
        return new LinkInfo
        {
            DisplayText = name,
            Url = string.Empty,
            Type = LinkType.Content,
            ImportanceScore = 100,
            HeaderType = HeaderType.TopLevelGroup
        };
    }
}
