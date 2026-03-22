// <copyright file="LinkNodeTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkNodeTests
{
    [Fact]
    public void CreateRoot_ReturnsRootNode()
    {
        // Act
        var root = LinkNode.CreateRoot();

        // Assert
        root.Should().NotBeNull();
        root.Parent.Should().BeNull();
        root.Depth.Should().Be(0);
        root.CollapseState.Should().Be(NodeCollapseState.Expanded);
        root.Link.DisplayText.Should().Be("Root");
    }

    [Fact]
    public void AddChild_CreatesChildWithCorrectDepth()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var linkInfo = CreateContentLink("https://example.com", "Test Link");

        // Act
        var child = root.AddChild(linkInfo);

        // Assert
        child.Depth.Should().Be(1);
        child.Parent.Should().Be(root);
        child.Link.Should().Be(linkInfo);
        root.Children.Should().Contain(child);
    }

    [Fact]
    public void AddChild_NestedChildren_HaveCorrectDepths()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var level1Link = CreateContentLink("https://example.com/1", "Level 1");
        var level2Link = CreateContentLink("https://example.com/2", "Level 2");
        var level3Link = CreateContentLink("https://example.com/3", "Level 3");

        // Act
        var level1 = root.AddChild(level1Link);
        var level2 = level1.AddChild(level2Link);
        var level3 = level2.AddChild(level3Link);

        // Assert
        level1.Depth.Should().Be(1);
        level2.Depth.Should().Be(2);
        level3.Depth.Should().Be(3);
    }

    [Fact]
    public void Expand_SetsStateToExpanded()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var linkInfo = CreateNavigationLink("https://example.com", "Nav Link");
        var child = root.AddChild(linkInfo);
        child.Collapse(); // Start collapsed

        // Act
        child.Expand();

        // Assert
        child.CollapseState.Should().Be(NodeCollapseState.Expanded);
    }

    [Fact]
    public void Collapse_SetsStateToCollapsed()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var linkInfo = CreateContentLink("https://example.com", "Content Link");
        var child = root.AddChild(linkInfo);

        // Act
        child.Collapse();

        // Assert
        child.CollapseState.Should().Be(NodeCollapseState.Collapsed);
    }

    [Fact]
    public void ToggleCollapse_SwitchesState()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var linkInfo = CreateContentLink("https://example.com", "Test Link");
        var child = root.AddChild(linkInfo);
        var initialState = child.CollapseState;

        // Act
        child.ToggleCollapse();

        // Assert
        child.CollapseState.Should().NotBe(initialState);

        // Toggle again
        child.ToggleCollapse();
        child.CollapseState.Should().Be(initialState);
    }

    [Fact]
    public void Select_MarksNodeAsSelected()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var linkInfo = CreateContentLink("https://example.com", "Test Link");
        var child = root.AddChild(linkInfo);

        // Act
        child.Select();

        // Assert
        child.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void Deselect_MarksNodeAsNotSelected()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var linkInfo = CreateContentLink("https://example.com", "Test Link");
        var child = root.AddChild(linkInfo);
        child.Select();

        // Act
        child.Deselect();

        // Assert
        child.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void GetVisibleDescendants_WhenExpanded_ReturnsAllChildren()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var child1 = root.AddChild(CreateContentLink("https://example.com/1", "Child 1"));
        var child2 = root.AddChild(CreateContentLink("https://example.com/2", "Child 2"));
        var child3 = root.AddChild(CreateContentLink("https://example.com/3", "Child 3"));

        // Act
        var visible = root.GetVisibleDescendants().ToList();

        // Assert
        visible.Should().HaveCount(3);
        visible.Should().Contain(child1);
        visible.Should().Contain(child2);
        visible.Should().Contain(child3);
    }

    [Fact]
    public void GetVisibleDescendants_WhenCollapsed_ReturnsEmpty()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        root.AddChild(CreateContentLink("https://example.com/1", "Child 1"));
        root.AddChild(CreateContentLink("https://example.com/2", "Child 2"));
        root.Collapse();

        // Act
        var visible = root.GetVisibleDescendants().ToList();

        // Assert
        visible.Should().BeEmpty();
    }

    [Fact]
    public void GetVisibleDescendants_NestedNodes_RespectsCollapseState()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var parent = root.AddChild(CreateContentLink("https://example.com/parent", "Parent"));
        parent.AddChild(CreateContentLink("https://example.com/child1", "Child 1"));
        parent.AddChild(CreateContentLink("https://example.com/child2", "Child 2"));

        // Parent expanded - should see all
        parent.Expand();
        var visibleExpanded = root.GetVisibleDescendants().ToList();
        visibleExpanded.Should().HaveCount(3);

        // Parent collapsed - should only see parent
        parent.Collapse();
        var visibleCollapsed = root.GetVisibleDescendants().ToList();
        visibleCollapsed.Should().HaveCount(1);
        visibleCollapsed.First().Should().Be(parent);
    }

    [Fact]
    public void GetAllDescendants_ReturnsAllRegardlessOfCollapseState()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var child1 = root.AddChild(CreateContentLink("https://example.com/1", "Child 1"));
        var child2 = root.AddChild(CreateContentLink("https://example.com/2", "Child 2"));
        var grandchild = child1.AddChild(CreateContentLink("https://example.com/gc", "Grandchild"));
        root.Collapse();
        child1.Collapse();

        // Act
        var all = root.GetAllDescendants().ToList();

        // Assert
        all.Should().HaveCount(3);
        all.Should().Contain(child1);
        all.Should().Contain(child2);
        all.Should().Contain(grandchild);
    }

    [Fact]
    public void CountDescendants_ReturnsCorrectCount()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var child1 = root.AddChild(CreateContentLink("https://example.com/1", "Child 1"));
        root.AddChild(CreateContentLink("https://example.com/2", "Child 2"));
        child1.AddChild(CreateContentLink("https://example.com/gc1", "Grandchild 1"));
        child1.AddChild(CreateContentLink("https://example.com/gc2", "Grandchild 2"));

        // Act
        var count = root.CountDescendants();

        // Assert
        count.Should().Be(4);
    }

    [Fact]
    public void ContentLink_StartsExpanded()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var contentLink = CreateContentLink("https://example.com", "Article Title");

        // Act
        var node = root.AddChild(contentLink);

        // Assert
        node.CollapseState.Should().Be(NodeCollapseState.Expanded);
    }

    [Fact]
    public void NavigationLink_StartsCollapsed()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var navLink = CreateNavigationLink("https://example.com", "Menu Item");

        // Act
        var node = root.AddChild(navLink);

        // Assert
        node.CollapseState.Should().Be(NodeCollapseState.Collapsed);
    }

    [Fact]
    public void FooterLink_StartsCollapsed()
    {
        // Arrange
        var root = LinkNode.CreateRoot();
        var footerLink = CreateFooterLink("https://example.com", "Privacy Policy");

        // Act
        var node = root.AddChild(footerLink);

        // Assert
        node.CollapseState.Should().Be(NodeCollapseState.Collapsed);
    }

    private static LinkInfo CreateContentLink(string url, string text) => new()
    {
        Url = url,
        DisplayText = text,
        Type = LinkType.Content,
        ImportanceScore = 80
    };

    private static LinkInfo CreateNavigationLink(string url, string text) => new()
    {
        Url = url,
        DisplayText = text,
        Type = LinkType.Navigation,
        ImportanceScore = 30
    };

    private static LinkInfo CreateFooterLink(string url, string text) => new()
    {
        Url = url,
        DisplayText = text,
        Type = LinkType.Footer,
        ImportanceScore = 10
    };
}
