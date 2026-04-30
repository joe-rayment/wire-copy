// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// AI-curated scrape result for a single page.
/// Identifies which links to remove (ads/promos) and the editorial-prominence
/// ordering of remaining story links. Persists per-domain via
/// <see cref="SiteHierarchyConfig"/>.
/// </summary>
public record AiCuratedResult
{
    /// <summary>
    /// Stable link keys (currently "url:&lt;absolute-url&gt;") of links that
    /// should be removed entirely from the tree (ads / promos / non-editorial).
    /// </summary>
    public required List<string> ExcludedLinkKeys { get; init; } = new();

    /// <summary>
    /// Stable link keys of remaining story links in the order the AI assigned
    /// (most editorially prominent first).
    /// </summary>
    public required List<string> StoryOrderLinkKeys { get; init; } = new();

    /// <summary>
    /// Optional semantic grouping. Empty list means flat ordering.
    /// </summary>
    public List<AiCuratedSection> Sections { get; init; } = new();

    /// <summary>
    /// When this curation was produced (UTC). Used for TTL invalidation.
    /// </summary>
    public required DateTime AnalyzedAt { get; init; }

    /// <summary>
    /// Build a stable key for a link. Currently keyed by URL.
    /// </summary>
    public static string KeyFor(string url) => $"url:{url}";
}

/// <summary>
/// Optional semantic grouping for AI-curated stories.
/// </summary>
public record AiCuratedSection
{
    /// <summary>Display name for the section (e.g., "Lead", "Opinion").</summary>
    public required string Name { get; init; }

    /// <summary>Ordered story link keys belonging to this section.</summary>
    public required List<string> StoryLinkKeys { get; init; } = new();

    /// <summary>Whether the section starts collapsed in the UI.</summary>
    public bool StartCollapsed { get; init; }
}
