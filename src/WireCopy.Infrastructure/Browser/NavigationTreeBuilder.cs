// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Builds hierarchical navigation trees from extracted links.
/// </summary>
public class NavigationTreeBuilder : INavigationTreeBuilder
{
    internal const int MaxContentLinks = 100;

    private readonly ILogger<NavigationTreeBuilder> _logger;

    public NavigationTreeBuilder(ILogger<NavigationTreeBuilder> logger)
    {
        _logger = logger;
    }

    public Task<NavigationTree> BuildTreeAsync(List<LinkInfo> links, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(BuildGroupedTree(links));
    }

    public Task<NavigationTree> BuildTreeAsync(
        List<LinkInfo> links,
        SiteHierarchyConfig hierarchyConfig,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(BuildWithHierarchyConfig(links, hierarchyConfig));
    }

    public Task<NavigationTree> BuildFromAiResultAsync(
        List<LinkInfo> links,
        AiCuratedResult curated,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(BuildFromAiCuratedResult(links, curated));
    }

    public NavigationTree BuildGroupedTree(List<LinkInfo> links)
    {
        var grouped = links
            .GroupBy(l => l.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

        var totalContent = grouped.GetValueOrDefault(LinkType.Content)?.Count ?? 0;
        if (totalContent > MaxContentLinks && grouped.TryGetValue(LinkType.Content, out var contentLinks))
        {
            grouped[LinkType.Content] = contentLinks.Take(MaxContentLinks).ToList();
            _logger.LogInformation("Capped content links from {Total} to {Max}", totalContent, MaxContentLinks);
        }

        var tree = NavigationTree.BuildWithGroups(grouped);

        _logger.LogInformation(
            "Built navigation tree with {Total} links: {Content} content, {Nav} navigation, {External} external, {Footer} footer",
            links.Count,
            grouped.GetValueOrDefault(LinkType.Content)?.Count ?? 0,
            grouped.GetValueOrDefault(LinkType.Navigation)?.Count ?? 0,
            grouped.GetValueOrDefault(LinkType.External)?.Count ?? 0,
            grouped.GetValueOrDefault(LinkType.Footer)?.Count ?? 0);

        return tree;
    }

    /// <summary>
    /// workspace-5oe9.1: durable exclusion test. A content link is dropped when
    /// its <see cref="LinkInfo.ParentSelector"/> contains any configured
    /// <see cref="SiteHierarchyConfig.ExcludeSelectors"/> fragment, or its
    /// <see cref="LinkInfo.Url"/> contains any
    /// <see cref="SiteHierarchyConfig.ExcludeUrlPatterns"/> substring
    /// (OrdinalIgnoreCase, mirroring <see cref="MatchesSection"/>).
    /// </summary>
    private static bool IsExcluded(LinkInfo link, SiteHierarchyConfig config)
    {
        if (link.ParentSelector != null && config.ExcludeSelectors.Count > 0 &&
            config.ExcludeSelectors.Any(s =>
                !string.IsNullOrEmpty(s) &&
                link.ParentSelector.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (config.ExcludeUrlPatterns.Count > 0 &&
            config.ExcludeUrlPatterns.Any(p =>
                !string.IsNullOrEmpty(p) &&
                link.Url.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool MatchesSection(LinkInfo link, HierarchySection section)
    {
        if (link.ParentSelector != null && section.ParentSelectors.Count > 0 &&
            section.ParentSelectors.Any(s => link.ParentSelector.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (section.UrlPatterns.Count > 0 &&
            section.UrlPatterns.Any(p => link.Url.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (link.SectionTitle != null &&
            string.Equals(link.SectionTitle, section.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private NavigationTree BuildWithHierarchyConfig(List<LinkInfo> links, SiteHierarchyConfig hierarchyConfig)
    {
        var root = LinkNode.CreateRoot();
        var matchedLinks = new HashSet<int>();
        var orderedSections = hierarchyConfig.Sections.OrderBy(s => s.SortOrder).ToList();

        var allContentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        if (allContentLinks.Count > MaxContentLinks)
        {
            _logger.LogInformation(
                "Capped content links from {Total} to {Max} (AI hierarchy)",
                allContentLinks.Count,
                MaxContentLinks);
        }

        var contentLinks = allContentLinks.Count > MaxContentLinks
            ? allContentLinks.Take(MaxContentLinks).ToList()
            : allContentLinks;
        var nonContentLinks = links.Where(l => l.Type != LinkType.Content).ToList();

        // workspace-5oe9.1: apply durable exclusion rules BEFORE tiering so
        // ads/promos identified by selector/url-pattern are dropped on every
        // visit (not just the snapshot the AI was run on). Uses the same
        // Contains semantics as MatchesSection.
        if (hierarchyConfig.ExcludeSelectors.Count > 0 || hierarchyConfig.ExcludeUrlPatterns.Count > 0)
        {
            var beforeExclude = contentLinks.Count;
            contentLinks = contentLinks.Where(l => !IsExcluded(l, hierarchyConfig)).ToList();
            var excludedCount = beforeExclude - contentLinks.Count;
            if (excludedCount > 0)
            {
                _logger.LogInformation(
                    "Excluded {Count} content link(s) via durable rules ({Selectors} selectors, {UrlPatterns} url-patterns)",
                    excludedCount,
                    hierarchyConfig.ExcludeSelectors.Count,
                    hierarchyConfig.ExcludeUrlPatterns.Count);
            }
        }

        foreach (var section in orderedSections)
        {
            var sectionLinks = new List<LinkInfo>();
            for (int i = 0; i < contentLinks.Count; i++)
            {
                if (matchedLinks.Contains(i))
                {
                    continue;
                }

                var link = contentLinks[i];
                if (MatchesSection(link, section))
                {
                    sectionLinks.Add(link);
                    matchedLinks.Add(i);
                }
            }

            if (sectionLinks.Count == 0)
            {
                continue;
            }

            var sectionHeader = LinkInfo.CreateSubSectionHeader(section.Name, LinkType.Content);
            var sectionNode = root.AddChild(sectionHeader);
            if (section.StartCollapsed)
            {
                sectionNode.Collapse();
            }

            foreach (var link in sectionLinks)
            {
                sectionNode.AddChild(link);
            }
        }

        // workspace-vwkt: drop unmatched content links rather than appending them to root.
        // The legacy AiHierarchical path used to dump unmatched links at the bottom of the
        // root node (under a vague "everything else" pile), which surfaced ad/junk links
        // beneath an AI-curated tree. The new AiCurated strategy filters destructively and
        // produces a cleaner tree; matching that semantics here keeps both paths consistent
        // and aligns with the user-visible promise of an AI-curated layout.
        var droppedUnmatched = contentLinks.Count - matchedLinks.Count;
        if (droppedUnmatched > 0)
        {
            _logger.LogInformation(
                "Dropped {Count} unmatched content link(s) from AI hierarchy tree (workspace-vwkt)",
                droppedUnmatched);
        }

        var nonContentGrouped = nonContentLinks.GroupBy(l => l.Type).ToDictionary(g => g.Key, g => g.ToList());
        var groupOrder = new[] { LinkType.Navigation, LinkType.External, LinkType.Footer };
        foreach (var linkType in groupOrder)
        {
            if (!nonContentGrouped.TryGetValue(linkType, out var groupLinks) || groupLinks.Count == 0)
            {
                continue;
            }

            var groupHeader = LinkInfo.CreateGroupHeader(linkType);
            var groupNode = root.AddChild(groupHeader);
            foreach (var link in groupLinks)
            {
                groupNode.AddChild(link);
            }
        }

        var tree = NavigationTree.BuildFromRoot(root);
        _logger.LogInformation(
            "Built AI hierarchy tree with {Total} links in {Sections} sections (config from {Model})",
            links.Count,
            orderedSections.Count,
            hierarchyConfig.ModelVersion);
        return tree;
    }

    /// <summary>
    /// Destructive-filter builder for the AI Curated strategy.
    /// </summary>
    private NavigationTree BuildFromAiCuratedResult(List<LinkInfo> links, AiCuratedResult curated)
    {
        var excluded = new HashSet<string>(curated.ExcludedLinkKeys, StringComparer.Ordinal);

        var contentLinks = links
            .Where(l => l.Type == LinkType.Content)
            .Where(l => !excluded.Contains(AiCuratedResult.KeyFor(l.Url)))
            .ToList();

        if (contentLinks.Count > MaxContentLinks)
        {
            _logger.LogInformation("Capped curated content links from {Total} to {Max}", contentLinks.Count, MaxContentLinks);
            contentLinks = contentLinks.Take(MaxContentLinks).ToList();
        }

        var nonContentLinks = links
            .Where(l => l.Type != LinkType.Content)
            .Where(l => !excluded.Contains(AiCuratedResult.KeyFor(l.Url)))
            .ToList();

        var byKey = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        foreach (var link in contentLinks)
        {
            byKey[AiCuratedResult.KeyFor(link.Url)] = link;
        }

        var orderedKeys = curated.StoryOrderLinkKeys
            .Where(byKey.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // workspace-5oe9.13 revisit safety-net: when a snapshot's ranked keys
        // match NONE of today's links, the snapshot has decayed as article URLs
        // rotated. Log it so the staleness is visible instead of silently
        // rendering document order. The durable pattern path is the real fix.
        if (orderedKeys.Count == 0 && curated.StoryOrderLinkKeys.Count > 0)
        {
            _logger.LogWarning(
                "AI-curated snapshot matched 0 of {Total} ranked links — stale snapshot, rendering document order; reconfigure via Ctrl+l",
                curated.StoryOrderLinkKeys.Count);
        }

        var orderedKeySet = new HashSet<string>(orderedKeys, StringComparer.Ordinal);

        var orderedContent = new List<LinkInfo>(contentLinks.Count);
        foreach (var key in orderedKeys)
        {
            orderedContent.Add(byKey[key]);
        }

        foreach (var link in contentLinks)
        {
            var k = AiCuratedResult.KeyFor(link.Url);
            if (!orderedKeySet.Contains(k))
            {
                orderedContent.Add(link);
            }
        }

        var root = LinkNode.CreateRoot();

        if (curated.Sections.Count > 0)
        {
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in curated.Sections)
            {
                var sectionStories = new List<LinkInfo>();
                foreach (var k in section.StoryLinkKeys)
                {
                    if (!byKey.TryGetValue(k, out var link))
                    {
                        continue;
                    }

                    if (assigned.Contains(k))
                    {
                        continue;
                    }

                    assigned.Add(k);
                    sectionStories.Add(link);
                }

                if (sectionStories.Count == 0)
                {
                    continue;
                }

                var sectionHeader = LinkInfo.CreateSubSectionHeader(section.Name, LinkType.Content);
                var sectionNode = root.AddChild(sectionHeader);
                if (section.StartCollapsed)
                {
                    sectionNode.Collapse();
                }

                foreach (var link in sectionStories)
                {
                    sectionNode.AddChild(link);
                }
            }

            foreach (var link in orderedContent.Where(l => !assigned.Contains(AiCuratedResult.KeyFor(l.Url))))
            {
                root.AddChild(link);
            }
        }
        else
        {
            foreach (var link in orderedContent)
            {
                root.AddChild(link);
            }
        }

        var nonContentGrouped = nonContentLinks.GroupBy(l => l.Type).ToDictionary(g => g.Key, g => g.ToList());
        var groupOrder = new[] { LinkType.Navigation, LinkType.External, LinkType.Footer };
        foreach (var linkType in groupOrder)
        {
            if (!nonContentGrouped.TryGetValue(linkType, out var groupLinks) || groupLinks.Count == 0)
            {
                continue;
            }

            var groupHeader = LinkInfo.CreateGroupHeader(linkType);
            var groupNode = root.AddChild(groupHeader);
            foreach (var link in groupLinks)
            {
                groupNode.AddChild(link);
            }
        }

        var tree = NavigationTree.BuildFromRoot(root);

        _logger.LogInformation(
            "Built AI-curated tree: {Stories} stories, {Excluded} excluded, {Sections} sections",
            orderedContent.Count,
            curated.ExcludedLinkKeys.Count,
            curated.Sections.Count);

        return tree;
    }
}
