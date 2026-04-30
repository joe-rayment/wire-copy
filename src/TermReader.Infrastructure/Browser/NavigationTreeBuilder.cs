// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

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

        for (int i = 0; i < contentLinks.Count; i++)
        {
            if (!matchedLinks.Contains(i))
            {
                root.AddChild(contentLinks[i]);
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
