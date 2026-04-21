// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Builds hierarchical navigation trees from extracted links.
/// Groups links by category with proper collapse state.
/// </summary>
public class NavigationTreeBuilder : INavigationTreeBuilder
{
    /// <summary>
    /// Maximum number of content links to include in a navigation tree.
    /// Prevents UI slowdowns on pages with hundreds of links.
    /// </summary>
    internal const int MaxContentLinks = 100;

    private readonly ILogger<NavigationTreeBuilder> _logger;

    public NavigationTreeBuilder(ILogger<NavigationTreeBuilder> logger)
    {
        _logger = logger;
    }

    public Task<NavigationTree> BuildTreeAsync(List<LinkInfo> links, CancellationToken cancellationToken = default)
    {
        var tree = BuildGroupedTree(links);
        return Task.FromResult(tree);
    }

    public Task<NavigationTree> BuildTreeAsync(
        List<LinkInfo> links,
        SiteHierarchyConfig hierarchyConfig,
        CancellationToken cancellationToken = default)
    {
        var tree = BuildWithHierarchyConfig(links, hierarchyConfig);
        return Task.FromResult(tree);
    }

    public NavigationTree BuildGroupedTree(List<LinkInfo> links)
    {
        // Group links by type
        var grouped = links
            .GroupBy(l => l.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Cap content links to prevent UI slowdowns on large pages
        var totalContent = grouped.GetValueOrDefault(LinkType.Content)?.Count ?? 0;
        if (totalContent > MaxContentLinks && grouped.TryGetValue(LinkType.Content, out var contentLinks))
        {
            grouped[LinkType.Content] = contentLinks.Take(MaxContentLinks).ToList();
            _logger.LogInformation(
                "Capped content links from {Total} to {Max} (document order)",
                totalContent,
                MaxContentLinks);
        }

        // Build hierarchical tree with group headers
        var tree = NavigationTree.BuildWithGroups(grouped);

        // Log summary
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
        // Match by parent selector
        if (link.ParentSelector != null && section.ParentSelectors.Count > 0 &&
            section.ParentSelectors.Any(s => link.ParentSelector.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Match by URL pattern (substring)
        if (section.UrlPatterns.Count > 0 &&
            section.UrlPatterns.Any(p => link.Url.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Match by section title
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
        var orderedSections = hierarchyConfig.Sections
            .OrderBy(s => s.SortOrder)
            .ToList();

        // Separate content links from non-content, capping content to MaxContentLinks
        var allContentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        if (allContentLinks.Count > MaxContentLinks)
        {
            _logger.LogInformation(
                "Capped content links from {Total} to {Max} (document order, AI hierarchy)",
                allContentLinks.Count,
                MaxContentLinks);
        }

        var contentLinks = allContentLinks.Count > MaxContentLinks
            ? allContentLinks.Take(MaxContentLinks).ToList()
            : allContentLinks;
        var nonContentLinks = links.Where(l => l.Type != LinkType.Content).ToList();

        // Build sections from AI config for content links
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

        // Add unmatched content links directly under root
        for (int i = 0; i < contentLinks.Count; i++)
        {
            if (!matchedLinks.Contains(i))
            {
                root.AddChild(contentLinks[i]);
            }
        }

        // Non-content links get standard group headers (collapsed)
        var nonContentGrouped = nonContentLinks
            .GroupBy(l => l.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

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
}
