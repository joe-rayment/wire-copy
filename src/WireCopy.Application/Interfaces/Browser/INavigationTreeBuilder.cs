// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

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
