// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-t1ok.2: the config-driven builders must consolidate Navigation +
/// Footer chrome into ONE collapsed "More" sub-menu (parity with the grouped
/// path's workspace-cn2g.3 behavior), and a saved FLAT DocumentOrder config must
/// be honored intentionally — exclusion rules applied, no false stale flag.
/// </summary>
[Trait("Category", "Unit")]
public class NavigationTreeBuilderMoreTests
{
    private readonly NavigationTreeBuilder _builder;

    public NavigationTreeBuilderMoreTests()
    {
        var logger = Substitute.For<ILogger<NavigationTreeBuilder>>();
        _builder = new NavigationTreeBuilder(logger);
    }

    private static LinkInfo Link(
        string url,
        string text,
        LinkType type = LinkType.Content,
        string? parentSelector = null,
        string? sectionTitle = null,
        int importance = 85)
    {
        return new LinkInfo
        {
            Url = url,
            DisplayText = text,
            Type = type,
            ImportanceScore = importance,
            ParentSelector = parentSelector,
            SectionTitle = sectionTitle,
        };
    }

    private static List<LinkInfo> LinksWithChrome()
    {
        return new List<LinkInfo>
        {
            Link("https://example.com/story-1", "Story one", parentSelector: "div.river a"),
            Link("https://example.com/story-2", "Story two", parentSelector: "div.river a"),
            Link("https://example.com/story-3", "Story three", parentSelector: "div.river a"),
            Link("https://example.com/nav-home", "Home", LinkType.Navigation),
            Link("https://example.com/nav-about", "About", LinkType.Navigation),
            Link("https://example.com/footer-privacy", "Privacy", LinkType.Footer),
            Link("https://other.example.org/elsewhere", "Elsewhere", LinkType.External),
        };
    }

    private static SiteHierarchyConfig Config(
        List<HierarchySection>? sections = null,
        LayoutKind kind = LayoutKind.AiCurated,
        List<string>? excludeSelectors = null,
        List<string>? excludeUrlPatterns = null,
        List<string>? excludeSectionTitles = null)
    {
        return new SiteHierarchyConfig
        {
            Domain = "example.com",
            UrlPattern = "^https?://example\\.com",
            Sections = sections ?? new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "test-model",
            Kind = kind,
            Version = 3,
            ExcludeSelectors = excludeSelectors ?? new List<string>(),
            ExcludeUrlPatterns = excludeUrlPatterns ?? new List<string>(),
            ExcludeSectionTitles = excludeSectionTitles ?? new List<string>(),
        };
    }

    private static LinkNode? FindGroup(NavigationTree tree, string displayText)
        => tree.Root.Children.FirstOrDefault(c =>
            c.IsGroupHeader && c.Link.DisplayText == displayText);

    [Fact]
    public async Task BuildTreeAsync_WithSections_ConsolidatesNavAndFooterIntoOneCollapsedMore()
    {
        var config = Config(sections: new List<HierarchySection>
        {
            new() { Name = "Stories", SortOrder = 0, ParentSelectors = new List<string> { "div.river" } },
        });

        var tree = await _builder.BuildTreeAsync(LinksWithChrome(), config);

        var more = FindGroup(tree, NavigationTree.MoreGroupLabel);
        more.Should().NotBeNull("nav + footer chrome must consolidate into one More sub-menu");
        more!.CollapseState.Should().Be(NodeCollapseState.Collapsed);
        more.Children.Select(c => c.Link.DisplayText)
            .Should().BeEquivalentTo(new[] { "Home", "About", "Privacy" });

        FindGroup(tree, "Navigation").Should().BeNull("Navigation merged into More");
        FindGroup(tree, "Footer").Should().BeNull("Footer merged into More");
        FindGroup(tree, "External").Should().NotBeNull("External stays its own group");
    }

    [Fact]
    public async Task BuildFromAiResultAsync_ConsolidatesNavAndFooterIntoOneCollapsedMore()
    {
        var links = LinksWithChrome();
        var curated = new AiCuratedResult
        {
            ExcludedLinkKeys = new List<string>(),
            StoryOrderLinkKeys = links
                .Where(l => l.Type == LinkType.Content)
                .Select(l => AiCuratedResult.KeyFor(l.Url))
                .ToList(),
            AnalyzedAt = DateTime.UtcNow,
        };

        var tree = await _builder.BuildFromAiResultAsync(links, curated);

        var more = FindGroup(tree, NavigationTree.MoreGroupLabel);
        more.Should().NotBeNull();
        more!.CollapseState.Should().Be(NodeCollapseState.Collapsed);
        more.Children.Select(c => c.Link.DisplayText)
            .Should().BeEquivalentTo(new[] { "Home", "About", "Privacy" });
        FindGroup(tree, "Navigation").Should().BeNull();
        FindGroup(tree, "Footer").Should().BeNull();
    }

    [Fact]
    public async Task BuildTreeAsync_FlatDocumentOrderConfig_AppliesExcludesWithoutStaleFlag()
    {
        var links = LinksWithChrome();
        links.Add(Link("https://example.com/sponsored/buy-now", "Buy now!", importance: 40));
        var config = Config(
            kind: LayoutKind.DocumentOrder,
            excludeUrlPatterns: new List<string> { "/sponsored/" });

        var tree = await _builder.BuildTreeAsync(links, config);

        tree.HierarchyConfigStale.Should().BeFalse(
            "a flat DocumentOrder config is an intentional layout, not a stale pattern");
        var allTexts = Flatten(tree.Root).Select(n => n.Link.DisplayText).ToList();
        allTexts.Should().NotContain("Buy now!", "the durable exclude must stick on the flat path");
        allTexts.Should().Contain("Story one");
        FindGroup(tree, NavigationTree.MoreGroupLabel).Should().NotBeNull(
            "the flat path routes through the grouped builder, which has the More menu");
    }

    [Fact]
    public async Task BuildTreeAsync_FlatDocumentOrderConfig_ExcludesBySectionTitle()
    {
        var links = LinksWithChrome();
        links.Add(Link(
            "https://example.com/promo",
            "Sponsored promo",
            sectionTitle: "Sponsor Posts",
            importance: 40));
        var config = Config(
            kind: LayoutKind.DocumentOrder,
            excludeSectionTitles: new List<string> { "Sponsor Posts" });

        var tree = await _builder.BuildTreeAsync(links, config);

        Flatten(tree.Root).Select(n => n.Link.DisplayText)
            .Should().NotContain("Sponsored promo");
        tree.HierarchyConfigStale.Should().BeFalse();
    }

    private static IEnumerable<LinkNode> Flatten(LinkNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var grandchild in Flatten(child))
            {
                yield return grandchild;
            }
        }
    }
}
