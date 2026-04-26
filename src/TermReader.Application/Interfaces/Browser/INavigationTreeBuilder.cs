// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Entities.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Service for building hierarchical navigation trees from extracted links.
/// </summary>
public interface INavigationTreeBuilder
{
    /// <summary>
    /// Builds a navigation tree from a list of links.
    /// Groups links by category and sets initial collapse state.
    /// </summary>
    /// <param name="links">List of extracted links.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Navigation tree with proper hierarchy.</returns>
    Task<NavigationTree> BuildTreeAsync(List<LinkInfo> links, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a navigation tree using an AI-generated hierarchy configuration.
    /// Sections and ordering come from the config rather than heuristic grouping.
    /// Falls back to BuildGroupedTree when a link doesn't match any section.
    /// </summary>
    /// <param name="links">List of extracted links.</param>
    /// <param name="hierarchyConfig">AI-generated hierarchy configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Navigation tree built from the hierarchy config.</returns>
    Task<NavigationTree> BuildTreeAsync(List<LinkInfo> links, SiteHierarchyConfig hierarchyConfig, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the tree with links grouped by their type.
    /// Content links appear first (expanded), followed by Navigation, Footer, External (collapsed).
    /// </summary>
    /// <param name="links">List of extracted links.</param>
    /// <returns>Navigation tree grouped by link type.</returns>
    NavigationTree BuildGroupedTree(List<LinkInfo> links);
}
