// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-5oe9.2 — keystone pin for the single most important property of
/// the AI-selection redesign: a pattern-based config (CSS selectors / URL
/// patterns) built while looking at one snapshot of a homepage KEEPS curating
/// a LATER snapshot whose article URLs have entirely rotated.
///
/// <para>POSITIVE: the durable BuildWithHierarchyConfig path generalizes —
/// a config describing snapshot A correctly partitions a disjoint snapshot B.</para>
/// <para>NEGATIVE (canary): the legacy per-URL AiCuratedResult snapshot path
/// has ZERO effect on a disjoint snapshot — every B link survives unexcluded
/// and the order equals B's document order. This green-documents the decay
/// that the redesign fixes; if AiCurated is ever re-pointed at absolute-URL
/// keys, B5/B14 fail, but this test stays as the layer-level explanation.</para>
/// </summary>
[Trait("Category", "Unit")]
public class DurablePatternMatchTests
{
    private readonly NavigationTreeBuilder _builder;

    public DurablePatternMatchTests()
    {
        var logger = Substitute.For<ILogger<NavigationTreeBuilder>>();
        _builder = new NavigationTreeBuilder(logger);
    }

    private const string LeadSelector = "main section.lead > h1 > a";
    private const string FeedSelector = "main section.feed ul li a";
    private const string AdSelector = "aside.ad a";

    private static LinkInfo Content(string url, string text, string parentSelector, string? sectionTitle = null) =>
        new()
        {
            Url = url,
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = 80,
            ParentSelector = parentSelector,
            SectionTitle = sectionTitle,
        };

    // Snapshot A — the page the AI "looked at" when the config was built.
    private static List<LinkInfo> SnapshotA() => new()
    {
        Content("https://news.example.com/2026/05/30/headline-a", "Headline A", LeadSelector, "Top Story"),
        Content("https://news.example.com/2026/05/30/feed-a1", "Feed A1", FeedSelector, "More News"),
        Content("https://news.example.com/2026/05/30/feed-a2", "Feed A2", FeedSelector, "More News"),
        Content("https://news.example.com/sponsored/promo-a", "Promo A", AdSelector),
    };

    // Snapshot B — a LATER visit. ENTIRELY different article URLs, IDENTICAL
    // structural fingerprints (ParentSelector / SectionTitle).
    private static List<LinkInfo> SnapshotB() => new()
    {
        Content("https://news.example.com/2026/06/15/headline-b", "Headline B", LeadSelector, "Top Story"),
        Content("https://news.example.com/2026/06/15/feed-b1", "Feed B1", FeedSelector, "More News"),
        Content("https://news.example.com/2026/06/15/feed-b2", "Feed B2", FeedSelector, "More News"),
        Content("https://news.example.com/sponsored/promo-b", "Promo B", AdSelector),
    };

    private static SiteHierarchyConfig PatternConfigFromA() => new()
    {
        Domain = "news.example.com",
        UrlPattern = "^https?://news\\.example\\.com/?$",
        Sections = new List<HierarchySection>
        {
            new() { Name = "Lead", SortOrder = 0, ParentSelectors = new List<string> { "section.lead" } },
            new() { Name = "Feed", SortOrder = 1, ParentSelectors = new List<string> { "section.feed" } },
        },
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "test-model",
    };

    [Fact]
    public async Task PatternConfig_BuiltOnSnapshotA_StillCuratesDisjointSnapshotB()
    {
        var a = SnapshotA();
        var b = SnapshotB();

        // Test-validity guard: A and B must share ZERO URLs, otherwise a
        // coincidental overlap could let a snapshot approach pass.
        var aUrls = a.Select(l => l.Url).ToHashSet();
        var bUrls = b.Select(l => l.Url).ToHashSet();
        aUrls.Overlaps(bUrls).Should().BeFalse("snapshots A and B must have entirely different URLs");

        var config = PatternConfigFromA();
        var tree = await _builder.BuildTreeAsync(b, config);

        // Same named sections, same SortOrder, applied to B.
        var topSections = tree.Root.Children
            .Where(n => n.IsGroupHeader)
            .Select(n => n.Link.DisplayText)
            .ToList();
        topSections.Should().ContainInOrder("Lead", "Feed");

        var lead = tree.Root.Children.First(n => n.IsGroupHeader && n.Link.DisplayText == "Lead");
        lead.Children.Should().ContainSingle();
        lead.Children[0].Link.Url.Should().Be("https://news.example.com/2026/06/15/headline-b");

        var feed = tree.Root.Children.First(n => n.IsGroupHeader && n.Link.DisplayText == "Feed");
        feed.Children.Select(c => c.Link.Url).Should().ContainInOrder(
            "https://news.example.com/2026/06/15/feed-b1",
            "https://news.example.com/2026/06/15/feed-b2");
    }

    [Fact]
    public async Task LegacyUrlSnapshot_BuiltOnSnapshotA_HasZeroEffectOnDisjointSnapshotB()
    {
        var a = SnapshotA();
        var b = SnapshotB();

        var aUrls = a.Select(l => l.Url).ToHashSet();
        var bUrls = b.Select(l => l.Url).ToHashSet();
        aUrls.Overlaps(bUrls).Should().BeFalse();

        // Build a curated result from A's ABSOLUTE URLs, with NO durable
        // Sections — exactly the shape the legacy AiCurated path persisted.
        var curatedFromA = new AiCuratedResult
        {
            StoryOrderLinkKeys = new List<string>
            {
                AiCuratedResult.KeyFor("https://news.example.com/2026/05/30/feed-a2"),
                AiCuratedResult.KeyFor("https://news.example.com/2026/05/30/headline-a"),
                AiCuratedResult.KeyFor("https://news.example.com/2026/05/30/feed-a1"),
            },
            ExcludedLinkKeys = new List<string>
            {
                AiCuratedResult.KeyFor("https://news.example.com/sponsored/promo-a"),
            },
            Sections = new List<AiCuratedSection>(), // EMPTY — the assertion below is exact
            AnalyzedAt = DateTime.UtcNow,
        };

        var tree = await _builder.BuildFromAiResultAsync(b, curatedFromA);

        var resultContentUrls = tree.GetAllNodes()
            .Where(n => !n.IsGroupHeader && n.Link.Type == LinkType.Content)
            .Select(n => n.Link.Url)
            .ToList();

        // Precise zero-effect property: NONE of B's links were excluded AND the
        // resulting content order equals B's DOCUMENT order (the A-derived
        // ranking + exclusion matched nothing in B).
        var bDocOrder = b.Where(l => l.Type == LinkType.Content).Select(l => l.Url).ToList();
        resultContentUrls.Should().Equal(bDocOrder);
    }
}
