// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Entities.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.Interfaces.Browser;

public interface INavigationTreeBuilder
{
    Task<NavigationTree> BuildTreeAsync(List<LinkInfo> links, CancellationToken cancellationToken = default);

    Task<NavigationTree> BuildTreeAsync(List<LinkInfo> links, SiteHierarchyConfig hierarchyConfig, CancellationToken cancellationToken = default);

    NavigationTree BuildGroupedTree(List<LinkInfo> links);

    /// <summary>
    /// Builds a navigation tree from an AI-curated result. Excluded links are
    /// REMOVED entirely (not pushed down). Stories are reordered by AI.
    /// </summary>
    Task<NavigationTree> BuildFromAiResultAsync(
        List<LinkInfo> links,
        AiCuratedResult curated,
        CancellationToken cancellationToken = default);
}
