// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-42q8.3 — the cursor→owning-section helper and section enumeration the
/// g s add-to-schedule card is built on: "which section is the user looking at,
/// and what other sections does this page have?"
/// </summary>
[Trait("Category", "Unit")]
public class NavigationTreeSectionHelperTests
{
    private static LinkInfo ContentLink(string text, string? sectionTitle = null) => new()
    {
        Url = $"https://example.com/{text.Replace(' ', '-').ToLowerInvariant()}",
        DisplayText = text,
        Type = LinkType.Content,
        ImportanceScore = 70,
        SectionTitle = sectionTitle,
    };

    private static NavigationTree SectionedTree() => NavigationTree.BuildWithGroups(new()
    {
        [LinkType.Content] = new()
        {
            ContentLink("A1", "World"),
            ContentLink("A2", "World"),
            ContentLink("B1", "Business"),
            ContentLink("B2", "Business"),
        },
    });

    private static NavigationTree FlatTree() => NavigationTree.BuildWithGroups(new()
    {
        [LinkType.Content] = new()
        {
            ContentLink("A1"),
            ContentLink("A2"),
            ContentLink("A3"),
        },
    });

    [Fact]
    public void SectionHeaders_SectionedTree_ReturnsHeadersInDocumentOrder()
    {
        var headers = SectionedTree().SectionHeaders;

        headers.Select(h => h.Link.DisplayText).Should().Equal("World", "Business");
        headers.Should().AllSatisfy(h => h.Link.HeaderType.Should().Be(HeaderType.SubSection));
    }

    [Fact]
    public void SectionHeaders_FlatTree_IsEmpty()
    {
        FlatTree().SectionHeaders.Should().BeEmpty();
    }

    [Fact]
    public void GetOwningSectionHeader_StoryInsideSection_ReturnsItsHeader()
    {
        var tree = SectionedTree();
        var businessStory = tree.Root.Children[1].Children[0];

        var owner = NavigationTree.GetOwningSectionHeader(businessStory);

        owner.Should().NotBeNull();
        owner!.Link.DisplayText.Should().Be("Business");
    }

    [Fact]
    public void GetOwningSectionHeader_OnTheHeaderItself_ReturnsThatHeader()
    {
        var tree = SectionedTree();
        var worldHeader = tree.Root.Children[0];

        NavigationTree.GetOwningSectionHeader(worldHeader).Should().BeSameAs(worldHeader);
    }

    [Fact]
    public void GetOwningSectionHeader_FlatTree_ReturnsNull()
    {
        var tree = FlatTree();

        NavigationTree.GetOwningSectionHeader(tree.CurrentSelection).Should().BeNull(
            "a flat tree has no sections — callers fall back to the whole page");
    }

    [Fact]
    public void GetOwningSectionHeader_NullNode_ReturnsNull()
    {
        NavigationTree.GetOwningSectionHeader(null).Should().BeNull();
    }

    [Fact]
    public void GetOwningSectionHeader_TracksTheCursorAsItMoves()
    {
        // The state CHANGE matters: moving the cursor from the first section into
        // the second must change the owning header the card would pre-fill.
        var tree = SectionedTree();
        NavigationTree.GetOwningSectionHeader(tree.CurrentSelection)!.Link.DisplayText.Should().Be("World");

        tree.SelectNext(); // A1
        tree.SelectNext(); // A2
        tree.SelectNext(); // Business header
        tree.SelectNext(); // B1

        NavigationTree.GetOwningSectionHeader(tree.CurrentSelection)!.Link.DisplayText.Should().Be("Business");
    }
}
