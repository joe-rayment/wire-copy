// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

public enum LayoutKind
{
    AiHierarchical,
    DocumentOrder,
    RssFeed,
    AiCurated,
}

public record SiteHierarchyConfig
{
    public required string Domain { get; init; }
    public required string UrlPattern { get; init; }
    public required List<HierarchySection> Sections { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string ModelVersion { get; init; }
    public LayoutKind Kind { get; init; } = LayoutKind.AiHierarchical;
    public string? RssFeedUrl { get; init; }
    public string? StructuralSignature { get; init; }
    public int Version { get; init; } = 1;
    public string? Strategy { get; init; }
    public AiCuratedResult? AiResult { get; init; }

    /// <summary>
    /// Durable exclusion rules (workspace-5oe9.1): content links whose
    /// <see cref="LinkInfo.ParentSelector"/> contains any of these fragments are
    /// dropped from the tree entirely. Unlike <see cref="AiCuratedResult.ExcludedLinkKeys"/>
    /// — which stores per-visit absolute URLs that go stale the moment the page
    /// changes — these selectors generalize across visits, so an AI-configured
    /// site keeps hiding ads/promos as the underlying articles rotate.
    /// </summary>
    public List<string> ExcludeSelectors { get; init; } = new();

    /// <summary>
    /// Durable exclusion rules (workspace-5oe9.1): content links whose
    /// <see cref="LinkInfo.Url"/> contains any of these substrings are dropped
    /// from the tree entirely (e.g. "/sponsored/", "/newsletter"). Generalizes
    /// across visits — see <see cref="ExcludeSelectors"/>.
    /// </summary>
    public List<string> ExcludeUrlPatterns { get; init; } = new();

    /// <summary>
    /// workspace-5oe9.5/.6: set when this config cannot be trusted to curate
    /// durably on a revisit — either a legacy per-URL snapshot (Version&lt;3,
    /// empty Sections, AiResult present) or a fresh analysis whose derived
    /// selectors failed the self-test. The UI surfaces it as a "re-run AI
    /// setup" nudge; a passive revisit never silently rots into document order
    /// without telling the user.
    /// </summary>
    public bool NeedsReanalyze { get; init; }
}

public record HierarchySection
{
    public required string Name { get; init; }
    public required int SortOrder { get; init; }
    public List<string> ParentSelectors { get; init; } = new();
    public List<string> UrlPatterns { get; init; } = new();
    public bool StartCollapsed { get; init; }

    /// <summary>
    /// workspace-9wm6: optional cap on how many matched links this section keeps
    /// (in document order). Null = unlimited. Used to pin a "lead" section to the
    /// single top story when its selector is too broad; the greedy tree builder
    /// then re-offers the overflow to later sections, so no story is lost.
    /// </summary>
    public int? MaxLinks { get; init; }
}
