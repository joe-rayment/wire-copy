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

    public NavigationTree BuildGroupedTree(List<LinkInfo> links)
    {
        // Group links by type
        var grouped = links
            .GroupBy(l => l.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

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
}
