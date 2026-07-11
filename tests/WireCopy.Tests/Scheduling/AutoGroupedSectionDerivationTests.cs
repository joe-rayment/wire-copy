// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-42q8.5 — DOM auto-groups become durable name-matched sections, and a
/// step pinned on one resolves against the same links via the heading-name tier.
/// </summary>
[Trait("Category", "Unit")]
public class AutoGroupedSectionDerivationTests
{
    private static LinkInfo ContentLink(string text, string? sectionTitle) => new()
    {
        Url = $"https://example.com/{text.Replace(' ', '-').ToLowerInvariant()}",
        DisplayText = text,
        Type = LinkType.Content,
        ImportanceScore = 70,
        SectionTitle = sectionTitle,
    };

    private static NavigationTree Tree(params LinkInfo[] links) =>
        NavigationTree.BuildWithGroups(new() { [LinkType.Content] = links.ToList() });

    [Fact]
    public void AutoGroupedTree_OneSectionPerGroup_InDocumentOrder()
    {
        var tree = Tree(
            ContentLink("A1", "World"),
            ContentLink("A2", "World"),
            ContentLink("B1", "Business"),
            ContentLink("B2", "Business"));

        var sections = AutoGroupedSectionDerivation.FromTree(tree);

        sections.Select(s => s.Name).Should().Equal("World", "Business");
        sections.Select(s => s.SortOrder).Should().Equal(0, 1);
        sections.Should().AllSatisfy(s => s.ParentSelectors.Should().BeEmpty("name-only match reproduces the auto-grouping exactly"));
    }

    [Fact]
    public void FlatTree_NoSections()
    {
        var tree = Tree(ContentLink("A1", null), ContentLink("A2", null), ContentLink("A3", null));

        AutoGroupedSectionDerivation.FromTree(tree).Should().BeEmpty();
    }

    [Fact]
    public void DerivedSection_ResolvesTheSameLinks_ViaTheHeadingNameTier()
    {
        // The full loop: derive from the visible tree, pin a step on section 2,
        // resolve against the SAME links — the schedule pulls what the user saw.
        var links = new[]
        {
            ContentLink("A1", "World"),
            ContentLink("A2", "World"),
            ContentLink("B1", "Business"),
            ContentLink("B2", "Business"),
        };
        var tree = Tree(links);
        var sections = AutoGroupedSectionDerivation.FromTree(tree);
        var config = new SiteHierarchyConfig
        {
            Domain = "example.com",
            UrlPattern = "^https?://(www\\.)?example\\.com/?",
            Sections = sections,
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "auto-grouped",
        };
        var step = ScheduleEditing.BuildStep(
            "https://example.com/", "example.com", config.UrlPattern, sections[1],
            Domain.Enums.Scheduling.TakeMode.WholeSection, null, required: true);

        var resolution = new SectionResolver().Resolve(config, links, step);

        resolution.Status.Should().Be(ResolutionStatus.Resolved);
        resolution.Tier.Should().Be(SectionMatchTier.HeadingName);
        resolution.Items.Select(i => i.Title).Should().Equal("B1", "B2");
    }

    [Fact]
    public void BlankAndDuplicateGroupNames_AreSkipped()
    {
        // Hand-build a root with a duplicate and a blank header — the derivation
        // must not emit sections that would double-match or match nothing.
        var root = LinkNode.CreateRoot();
        var world1 = root.AddChild(LinkInfo.CreateSubSectionHeader("World", LinkType.Content));
        world1.AddChild(ContentLink("A1", "World"));
        var blank = root.AddChild(LinkInfo.CreateSubSectionHeader("   ", LinkType.Content));
        blank.AddChild(ContentLink("X1", "   "));
        var world2 = root.AddChild(LinkInfo.CreateSubSectionHeader("world", LinkType.Content));
        world2.AddChild(ContentLink("A2", "world"));
        var tree = NavigationTree.BuildFromRoot(root);

        var sections = AutoGroupedSectionDerivation.FromTree(tree);

        sections.Should().ContainSingle().Which.Name.Should().Be("World");
    }
}
