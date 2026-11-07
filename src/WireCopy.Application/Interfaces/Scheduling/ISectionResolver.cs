// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>
/// workspace-frpl.3 — resolves a recipe step's DURABLE section reference against
/// the links extracted on a given visit, riding the same selector/url-pattern
/// matching as the durable layout config. Pure (no I/O) and deterministic.
/// </summary>
public interface ISectionResolver
{
    SectionResolution Resolve(SiteHierarchyConfig config, IReadOnlyList<LinkInfo> todaysLinks, RecipeStep step);

    /// <summary>
    /// S4 POSITION — the 1-based index of <paramref name="url"/> in the step's WholeSection list
    /// (document order, excludes applied, capped), or null when absent. URLs compare
    /// OrdinalIgnoreCase, the same as the resolver's de-duplication.
    /// </summary>
    int? PositionOf(SiteHierarchyConfig config, IReadOnlyList<LinkInfo> todaysLinks, RecipeStep sectionStep, string url);
}
