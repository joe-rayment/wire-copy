// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// Layout strategy for rendering a page's link list.
/// </summary>
public enum LayoutKind
{
    /// <summary>
    /// AI-generated hierarchy with named sections.
    /// </summary>
    AiHierarchical,

    /// <summary>
    /// Document-order grouping by link type (Content, Navigation, Footer, External).
    /// </summary>
    DocumentOrder,

    /// <summary>
    /// Chronological list from an RSS/Atom feed.
    /// </summary>
    RssFeed,
}

/// <summary>
/// Saved hierarchy configuration for a website.
/// Maps URL patterns to visual sections with link groupings and ordering.
/// </summary>
public record SiteHierarchyConfig
{
    /// <summary>
    /// Domain this config applies to (e.g., "nytimes.com").
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Regex pattern matching URLs this config applies to.
    /// Example: "^https://www\\.nytimes\\.com/?$" for homepage only.
    /// </summary>
    public required string UrlPattern { get; init; }

    /// <summary>
    /// Ordered list of visual sections detected on the page.
    /// </summary>
    public required List<HierarchySection> Sections { get; init; }

    /// <summary>
    /// When this config was created/last updated.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Model identifier that generated this config (for staleness tracking).
    /// </summary>
    public required string ModelVersion { get; init; }

    /// <summary>
    /// Layout strategy that produced this config.
    /// </summary>
    public LayoutKind Kind { get; init; } = LayoutKind.AiHierarchical;

    /// <summary>
    /// RSS feed URL (only set when Kind is RssFeed).
    /// </summary>
    public string? RssFeedUrl { get; init; }

    /// <summary>
    /// Hash of section structure for layout de-duplication.
    /// Format: sorted (section name + link count) pairs.
    /// </summary>
    public string? StructuralSignature { get; init; }
}

/// <summary>
/// A visual section on a page as determined by AI analysis.
/// Groups related links under a named heading.
/// </summary>
public record HierarchySection
{
    /// <summary>
    /// Display name for this section (e.g., "Top Stories", "Opinion", "Trending").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Sort order for display (lower = higher on page).
    /// </summary>
    public required int SortOrder { get; init; }

    /// <summary>
    /// CSS parent selectors that identify links belonging to this section.
    /// Matched against LinkInfo.ParentSelector.
    /// </summary>
    public List<string> ParentSelectors { get; init; } = new();

    /// <summary>
    /// URL patterns (substring match) for links in this section.
    /// Used as fallback when ParentSelector matching is insufficient.
    /// </summary>
    public List<string> UrlPatterns { get; init; } = new();

    /// <summary>
    /// Whether this section should start collapsed in the UI.
    /// </summary>
    public bool StartCollapsed { get; init; }
}
