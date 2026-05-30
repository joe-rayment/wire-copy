// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-5oe9.5 — the page-load routing decision shared by BuildPage and
/// RebuildFromBuildCacheAsync. The load-bearing property is that durable
/// Sections route to the pattern path FIRST and store-agnostically, so an
/// AiCurated config never collapses to the stale URL-snapshot path.
/// </summary>
[Trait("Category", "Unit")]
public class HierarchyRouteResolverTests
{
    private static SiteHierarchyConfig Config(
        int sectionCount = 0,
        LayoutKind kind = LayoutKind.AiHierarchical,
        string? strategy = null,
        AiCuratedResult? aiResult = null,
        string? rssUrl = null)
    {
        var sections = new List<HierarchySection>();
        for (var i = 0; i < sectionCount; i++)
        {
            sections.Add(new HierarchySection { Name = $"S{i}", SortOrder = i, ParentSelectors = new List<string> { ".x" } });
        }

        return new SiteHierarchyConfig
        {
            Domain = "x.com",
            UrlPattern = "^x$",
            Sections = sections,
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "test",
            Kind = kind,
            Strategy = strategy,
            AiResult = aiResult,
            RssFeedUrl = rssUrl,
        };
    }

    private static AiCuratedResult Snapshot() => new()
    {
        ExcludedLinkKeys = new List<string>(),
        StoryOrderLinkKeys = new List<string> { "url:https://x.com/a" },
        AnalyzedAt = DateTime.UtcNow,
    };

    [Fact]
    public void NullConfig_RoutesToDocumentOrder()
        => HierarchyRouteResolver.Decide(null).Should().Be(HierarchyRoute.DocumentOrder);

    [Fact]
    public void SectionsPresent_WinOverAiResult_StoreAgnostic()
    {
        // The decisive fix: an AiCurated config with BOTH durable Sections and a
        // lingering AiResult routes to the pattern path, not the snapshot path.
        var config = Config(sectionCount: 2, kind: LayoutKind.AiCurated, strategy: "AiCurated", aiResult: Snapshot());
        HierarchyRouteResolver.Decide(config).Should().Be(HierarchyRoute.PatternConfig);
    }

    [Fact]
    public void RehydratedConfig_KindDefaulted_StrategyNull_AiResultNull_StillPattern()
    {
        // Mimics a DiskCacheStore rehydrate: Kind defaults to AiHierarchical,
        // Strategy null, AiResult dropped — but Sections survive.
        var config = Config(sectionCount: 1, kind: LayoutKind.AiHierarchical, strategy: null, aiResult: null);
        HierarchyRouteResolver.Decide(config).Should().Be(HierarchyRoute.PatternConfig);
    }

    [Fact]
    public void SnapshotOnly_NoSections_RoutesToAiSnapshot()
    {
        var config = Config(sectionCount: 0, kind: LayoutKind.AiCurated, strategy: "AiCurated", aiResult: Snapshot());
        HierarchyRouteResolver.Decide(config).Should().Be(HierarchyRoute.AiSnapshot);
    }

    [Fact]
    public void RssConfig_NoSections_RoutesToRss()
    {
        var config = Config(sectionCount: 0, kind: LayoutKind.RssFeed, rssUrl: "https://x.com/feed.xml");
        HierarchyRouteResolver.Decide(config).Should().Be(HierarchyRoute.RssFeed);
    }
}
