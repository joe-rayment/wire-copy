// Educational and personal use only.

using FluentAssertions;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

public class NavigationTreeSubGroupTests
{
    #region Multiple sections — sub-grouping applied

    [Fact]
    public void BuildWithGroups_MultipleSections_CreatesSubSectionHeaders()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("Article 1", "World"),
                ContentLink("Article 2", "World"),
                ContentLink("Article 3", "Business"),
                ContentLink("Article 4", "Business"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        var rootChildren = tree.Root.Children;
        rootChildren.Should().HaveCount(2, "two sub-section headers");

        rootChildren[0].Link.DisplayText.Should().Be("World");
        rootChildren[0].Link.HeaderType.Should().Be(HeaderType.SubSection);
        rootChildren[0].Children.Should().HaveCount(2);

        rootChildren[1].Link.DisplayText.Should().Be("Business");
        rootChildren[1].Link.HeaderType.Should().Be(HeaderType.SubSection);
        rootChildren[1].Children.Should().HaveCount(2);
    }

    [Fact]
    public void BuildWithGroups_MultipleSections_PreservesDocumentOrder()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("First", "Sports"),
                ContentLink("Second", "Sports"),
                ContentLink("Third", "Opinion"),
                ContentLink("Fourth", "Opinion"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        tree.Root.Children[0].Link.DisplayText.Should().Be("Sports");
        tree.Root.Children[1].Link.DisplayText.Should().Be("Opinion");
    }

    [Fact]
    public void BuildWithGroups_SubSectionHeaders_AreContentType()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("A1", "Section A"),
                ContentLink("A2", "Section A"),
                ContentLink("B1", "Section B"),
                ContentLink("B2", "Section B"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        foreach (var child in tree.Root.Children)
        {
            child.Link.Type.Should().Be(LinkType.Content);
            child.Link.HeaderType.Should().Be(HeaderType.SubSection);
        }
    }

    [Fact]
    public void BuildWithGroups_SubSectionHeaders_StartExpanded()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("A1", "Section A"),
                ContentLink("A2", "Section A"),
                ContentLink("B1", "Section B"),
                ContentLink("B2", "Section B"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        foreach (var child in tree.Root.Children)
        {
            child.CollapseState.Should().Be(NodeCollapseState.Expanded,
                "content sub-section headers should start expanded");
        }
    }

    #endregion

    #region Single section — suppressed

    [Fact]
    public void BuildWithGroups_SingleSection_NoSubGrouping()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("Article 1", "World"),
                ContentLink("Article 2", "World"),
                ContentLink("Article 3", "World"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        // All links should be direct children of root — no sub-section header
        tree.Root.Children.Should().HaveCount(3);
        tree.Root.Children.All(c => !c.IsGroupHeader).Should().BeTrue(
            "single section should be suppressed");
    }

    #endregion

    #region No sections — flat structure

    [Fact]
    public void BuildWithGroups_NoSectionTitles_FlatStructure()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("Article 1"),
                ContentLink("Article 2"),
                ContentLink("Article 3"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        tree.Root.Children.Should().HaveCount(3);
        tree.Root.Children.All(c => !c.IsGroupHeader).Should().BeTrue();
    }

    #endregion

    #region Mixed null and named sections

    [Fact]
    public void BuildWithGroups_NullBeforeNamedSections_HeaderlessFlow()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("Headerless 1"),
                ContentLink("Headerless 2"),
                ContentLink("World 1", "World"),
                ContentLink("World 2", "World"),
                ContentLink("Sports 1", "Sports"),
                ContentLink("Sports 2", "Sports"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        // First two should be direct children (headerless flow)
        var rootChildren = tree.Root.Children;
        rootChildren[0].Link.DisplayText.Should().Be("Headerless 1");
        rootChildren[0].IsGroupHeader.Should().BeFalse();
        rootChildren[1].Link.DisplayText.Should().Be("Headerless 2");
        rootChildren[1].IsGroupHeader.Should().BeFalse();

        // Then sub-section headers
        rootChildren[2].Link.DisplayText.Should().Be("World");
        rootChildren[2].IsGroupHeader.Should().BeTrue();
        rootChildren[2].Children.Should().HaveCount(2);

        rootChildren[3].Link.DisplayText.Should().Be("Sports");
        rootChildren[3].IsGroupHeader.Should().BeTrue();
        rootChildren[3].Children.Should().HaveCount(2);
    }

    [Fact]
    public void BuildWithGroups_NullAfterNamedSection_PlacedInPrecedingSection()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("World 1", "World"),
                ContentLink("World 2", "World"),
                ContentLink("No Section"),
                ContentLink("Sports 1", "Sports"),
                ContentLink("Sports 2", "Sports"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        var rootChildren = tree.Root.Children;

        // "No Section" link goes under the preceding World section
        rootChildren[0].Link.DisplayText.Should().Be("World");
        rootChildren[0].Children.Should().HaveCount(3);
        rootChildren[0].Children[2].Link.DisplayText.Should().Be("No Section");

        rootChildren[1].Link.DisplayText.Should().Be("Sports");
        rootChildren[1].Children.Should().HaveCount(2);
    }

    #endregion

    #region Partial extraction — insufficient links per section

    [Fact]
    public void BuildWithGroups_InsufficientLinksPerSection_NoSubGrouping()
    {
        // 3 sections with 1 link each = avg 1 link/section < 2
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("A", "Section A"),
                ContentLink("B", "Section B"),
                ContentLink("C", "Section C"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        // Should fall back to flat structure
        tree.Root.Children.Should().HaveCount(3);
        tree.Root.Children.All(c => !c.IsGroupHeader).Should().BeTrue(
            "average of 1 link per section is below the threshold of 2");
    }

    [Fact]
    public void BuildWithGroups_ExactlyTwoLinksPerSection_SubGroups()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("A1", "Section A"),
                ContentLink("A2", "Section A"),
                ContentLink("B1", "Section B"),
                ContentLink("B2", "Section B"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        tree.Root.Children.Should().HaveCount(2);
        tree.Root.Children.All(c => c.IsGroupHeader).Should().BeTrue();
    }

    #endregion

    #region Non-content groups remain unchanged

    [Fact]
    public void BuildWithGroups_NonContentGroups_Unaffected()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("C1", "World"),
                ContentLink("C2", "World"),
                ContentLink("C3", "Sports"),
                ContentLink("C4", "Sports"),
            },
            [LinkType.Navigation] = new()
            {
                NavLink("Home"),
                NavLink("About"),
            },
            [LinkType.Footer] = new()
            {
                FooterLink("Privacy"),
            },
        };

        var tree = NavigationTree.BuildWithGroups(links);

        var rootChildren = tree.Root.Children;

        // Content sub-sections first
        rootChildren[0].Link.DisplayText.Should().Be("World");
        rootChildren[0].Link.HeaderType.Should().Be(HeaderType.SubSection);

        rootChildren[1].Link.DisplayText.Should().Be("Sports");
        rootChildren[1].Link.HeaderType.Should().Be(HeaderType.SubSection);

        // Navigation group header
        rootChildren[2].Link.DisplayText.Should().Be("Navigation");
        rootChildren[2].Link.HeaderType.Should().Be(HeaderType.TopLevelGroup);
        rootChildren[2].Children.Should().HaveCount(2);

        // Footer group header
        rootChildren[3].Link.DisplayText.Should().Be("Footer");
        rootChildren[3].Link.HeaderType.Should().Be(HeaderType.TopLevelGroup);
        rootChildren[3].Children.Should().HaveCount(1);
    }

    #endregion

    #region Tree depth

    [Fact]
    public void BuildWithGroups_WithSubSections_CorrectDepth()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("A1", "Section A"),
                ContentLink("A2", "Section A"),
                ContentLink("B1", "Section B"),
                ContentLink("B2", "Section B"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        // Root = 0, sub-section headers = 1, links = 2
        tree.Root.Depth.Should().Be(0);

        foreach (var sectionNode in tree.Root.Children)
        {
            sectionNode.Depth.Should().Be(1);
            foreach (var linkNode in sectionNode.Children)
            {
                linkNode.Depth.Should().Be(2);
            }
        }
    }

    [Fact]
    public void BuildWithGroups_FlatContent_DepthOne()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("A"),
                ContentLink("B"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        foreach (var child in tree.Root.Children)
        {
            child.Depth.Should().Be(1);
        }
    }

    #endregion

    #region TotalLinks count

    [Fact]
    public void BuildWithGroups_WithSubSections_TotalLinksExcludesHeaders()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("A1", "Section A"),
                ContentLink("A2", "Section A"),
                ContentLink("B1", "Section B"),
                ContentLink("B2", "Section B"),
            }
        };

        var tree = NavigationTree.BuildWithGroups(links);

        // 4 actual links, 2 sub-section headers excluded
        tree.TotalLinks.Should().Be(4);
    }

    #endregion

    #region Helpers

    private static LinkInfo ContentLink(string text, string? sectionTitle = null)
    {
        return new LinkInfo
        {
            Url = $"https://example.com/{text.Replace(' ', '-').ToLowerInvariant()}",
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = 70,
            SectionTitle = sectionTitle,
        };
    }

    private static LinkInfo NavLink(string text)
    {
        return new LinkInfo
        {
            Url = $"https://example.com/{text.Replace(' ', '-').ToLowerInvariant()}",
            DisplayText = text,
            Type = LinkType.Navigation,
            ImportanceScore = 30,
        };
    }

    private static LinkInfo FooterLink(string text)
    {
        return new LinkInfo
        {
            Url = $"https://example.com/{text.Replace(' ', '-').ToLowerInvariant()}",
            DisplayText = text,
            Type = LinkType.Footer,
            ImportanceScore = 10,
        };
    }

    #endregion
}
