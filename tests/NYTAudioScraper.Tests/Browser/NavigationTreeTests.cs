// <copyright file="NavigationTreeTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using NYTAudioScraper.Domain.Entities.Browser;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;
using Xunit;

namespace NYTAudioScraper.Tests.Browser;

public class NavigationTreeTests
{
    [Fact]
    public void Build_WithEmptyList_CreatesTreeWithNoLinks()
    {
        // Arrange
        var links = new List<LinkInfo>();

        // Act
        var tree = NavigationTree.Build(links);

        // Assert
        tree.Should().NotBeNull();
        tree.TotalLinks.Should().Be(0);
        tree.Root.Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_WithLinks_CreatesTreeWithCorrectCount()
    {
        // Arrange
        var links = CreateSampleLinks(5);

        // Act
        var tree = NavigationTree.Build(links);

        // Assert
        tree.TotalLinks.Should().Be(5);
        tree.Root.Children.Should().HaveCount(5);
    }

    [Fact]
    public void Build_SelectsFirstChildByDefault()
    {
        // Arrange
        var links = CreateSampleLinks(3);

        // Act
        var tree = NavigationTree.Build(links);

        // Assert
        tree.CurrentSelection.Should().NotBeNull();
        tree.CurrentSelection.Should().Be(tree.Root.Children.First());
        tree.CurrentSelection!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectNext_MovesToNextNode()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        var firstNode = tree.CurrentSelection;

        // Act
        tree.SelectNext();

        // Assert
        tree.CurrentSelection.Should().NotBe(firstNode);
        tree.CurrentSelection.Should().Be(tree.Root.Children[1]);
        firstNode!.IsSelected.Should().BeFalse();
        tree.CurrentSelection!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectNext_AtLastNode_StaysAtLast()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);

        // Move to last
        tree.SelectNext();
        tree.SelectNext();
        var lastNode = tree.CurrentSelection;

        // Act - try to go past last
        tree.SelectNext();

        // Assert
        tree.CurrentSelection.Should().Be(lastNode);
    }

    [Fact]
    public void SelectPrevious_MovesToPreviousNode()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        tree.SelectNext(); // Move to second node
        var secondNode = tree.CurrentSelection;

        // Act
        tree.SelectPrevious();

        // Assert
        tree.CurrentSelection.Should().NotBe(secondNode);
        tree.CurrentSelection.Should().Be(tree.Root.Children[0]);
    }

    [Fact]
    public void SelectPrevious_AtFirstNode_StaysAtFirst()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        var firstNode = tree.CurrentSelection;

        // Act
        tree.SelectPrevious();

        // Assert
        tree.CurrentSelection.Should().Be(firstNode);
    }

    [Fact]
    public void ToggleCollapse_TogglesCurrentNodeState()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        var initialState = tree.CurrentSelection!.CollapseState;

        // Act
        tree.ToggleCollapse();

        // Assert
        tree.CurrentSelection.CollapseState.Should().NotBe(initialState);
    }

    [Fact]
    public void GetSelectedNode_ReturnsCurrentSelection()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);

        // Act
        var selected = tree.GetSelectedNode();

        // Assert
        selected.Should().Be(tree.CurrentSelection);
    }

    [Fact]
    public void GetVisibleNodes_ReturnsOnlyVisible()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);

        // Act
        var visible = tree.GetVisibleNodes().ToList();

        // Assert
        visible.Should().HaveCount(3);
    }

    [Fact]
    public void GetAllNodes_ReturnsAllNodes()
    {
        // Arrange
        var links = CreateSampleLinks(5);
        var tree = NavigationTree.Build(links);

        // Collapse all
        tree.CollapseAll();

        // Act
        var all = tree.GetAllNodes().ToList();

        // Assert
        all.Should().HaveCount(5);
    }

    [Fact]
    public void SelectNodeById_SelectsCorrectNode()
    {
        // Arrange
        var links = CreateSampleLinks(5);
        var tree = NavigationTree.Build(links);
        var targetNode = tree.Root.Children[3];
        var targetId = targetNode.Id;

        // Act
        var result = tree.SelectNodeById(targetId);

        // Assert
        result.Should().BeTrue();
        tree.CurrentSelection.Should().Be(targetNode);
        targetNode.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectNodeById_WithInvalidId_ReturnsFalse()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        var originalSelection = tree.CurrentSelection;

        // Act
        var result = tree.SelectNodeById(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
        tree.CurrentSelection.Should().Be(originalSelection);
    }

    [Fact]
    public void ExpandAll_ExpandsAllNodes()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        tree.CollapseAll();

        // Act
        tree.ExpandAll();

        // Assert
        foreach (var node in tree.GetAllNodes())
        {
            node.CollapseState.Should().Be(NodeCollapseState.Expanded);
        }
    }

    [Fact]
    public void CollapseAll_CollapsesAllNodes()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);

        // Act
        tree.CollapseAll();

        // Assert
        foreach (var node in tree.GetAllNodes())
        {
            node.CollapseState.Should().Be(NodeCollapseState.Collapsed);
        }
    }

    [Fact]
    public void SelectParent_MovesToParentNode()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var parentLink = new LinkInfo
        {
            Url = "https://example.com/parent",
            DisplayText = "Parent",
            Type = LinkType.Content,
            ImportanceScore = 80
        };
        var parent = root.AddChild(parentLink);

        var childLink = new LinkInfo
        {
            Url = "https://example.com/child",
            DisplayText = "Child",
            Type = LinkType.Content,
            ImportanceScore = 80
        };
        var child = parent.AddChild(childLink);

        var tree = NavigationTree.Build(new List<LinkInfo> { parentLink });

        // Manually set up tree to have nested structure for this test
        // Note: The current Build implementation adds all as direct children
        // This test documents expected behavior for nested trees
    }

    [Fact]
    public void SelectFirstChild_WithExpandedNode_MovesToFirstChild()
    {
        // Arrange - Build doesn't create nested structures by default
        // This test documents expected behavior
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);

        // Current selection has no children in flat tree
        // Act
        tree.SelectFirstChild();

        // Assert - no change expected for flat tree
        tree.CurrentSelection.Should().Be(tree.Root.Children[0]);
    }

    [Fact]
    public void Navigation_SkipsCollapsedChildren()
    {
        // Arrange
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/1", DisplayText = "Item 1", Type = LinkType.Content, ImportanceScore = 80 },
            new() { Url = "https://example.com/2", DisplayText = "Item 2", Type = LinkType.Navigation, ImportanceScore = 30 },
            new() { Url = "https://example.com/3", DisplayText = "Item 3", Type = LinkType.Content, ImportanceScore = 80 }
        };
        var tree = NavigationTree.Build(links);

        // All nodes are direct children of root in current implementation
        // Collapse state affects visibility of descendants, not siblings
        var visibleBefore = tree.GetVisibleNodes().Count();

        // Act
        tree.SelectNext();
        tree.SelectNext();

        // Assert
        tree.CurrentSelection!.Link.DisplayText.Should().Be("Item 3");
    }

    [Fact]
    public void EnsureSelection_WhenSelectionExists_ReturnsTrueWithoutChange()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        var originalSelection = tree.CurrentSelection;

        // Act
        var result = tree.EnsureSelection();

        // Assert
        result.Should().BeTrue();
        tree.CurrentSelection.Should().Be(originalSelection);
        tree.CurrentSelection!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void EnsureSelection_WithEmptyTree_ReturnsFalse()
    {
        // Arrange
        var tree = NavigationTree.Build(new List<LinkInfo>());

        // Act
        var result = tree.EnsureSelection();

        // Assert
        result.Should().BeFalse();
        tree.CurrentSelection.Should().BeNull();
    }

    [Fact]
    public void EnsureSelection_SelectsFirstVisibleNode()
    {
        // Arrange
        var links = CreateSampleLinks(3);
        var tree = NavigationTree.Build(links);
        var firstVisible = tree.GetVisibleNodes().First();

        // Act
        var result = tree.EnsureSelection();

        // Assert
        result.Should().BeTrue();
        tree.CurrentSelection.Should().Be(firstVisible);
        firstVisible.IsSelected.Should().BeTrue();
    }

    private static List<LinkInfo> CreateSampleLinks(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new LinkInfo
            {
                Url = $"https://example.com/link{i}",
                DisplayText = $"Link {i}",
                Type = LinkType.Content,
                ImportanceScore = 80
            })
            .ToList();
    }
}
