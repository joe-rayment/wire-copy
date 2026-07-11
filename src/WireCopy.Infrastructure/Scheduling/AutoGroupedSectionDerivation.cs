// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-42q8.5 — turns the DOM-detected groups of an UNCONFIGURED page (the
/// auto-grouping <c>NavigationTree.BuildWithGroups</c> applies from
/// <c>LinkInfo.SectionTitle</c>) into durable <see cref="HierarchySection"/>s, so
/// the sections the user SEES are pinnable by a schedule without an AI setup pass.
///
/// Deliberately NAME-ONLY: <c>NavigationTreeBuilder.MatchesBySectionTitle</c> makes
/// a section with no selectors match links by live heading text, which reproduces
/// the auto-grouping exactly on revisit (WYSIWYG) — whereas selectors derived from
/// uniform list markup would be near-identical across groups and mis-attribute via
/// the greedy overlap. The trade-off is honest: a renamed heading resolves to a
/// loud ZeroMatch (surfaced by the badge / "needs reconfigure"), and the g l wizard
/// remains the upgrade path for AI-grade selector durability.
/// </summary>
internal static class AutoGroupedSectionDerivation
{
    /// <summary>
    /// One section per detected group, in document order; empty for a flat tree.
    /// Blank and duplicate (case-insensitive) group names are skipped — a duplicate
    /// name would double-match the same links.
    /// </summary>
    public static List<HierarchySection> FromTree(NavigationTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var sections = new List<HierarchySection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in tree.SectionHeaders)
        {
            var name = header.Link.DisplayText;
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            sections.Add(new HierarchySection
            {
                Name = name.Trim(),
                SortOrder = sections.Count,
            });
        }

        return sections;
    }
}
