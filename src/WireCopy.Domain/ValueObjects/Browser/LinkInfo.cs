// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// Represents metadata about a hyperlink extracted from a web page.
/// Immutable value object.
/// </summary>
public record LinkInfo
{
    /// <summary>
    /// Absolute URL of the link.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Display text of the link (anchor text).
    /// </summary>
    public required string DisplayText { get; init; }

    /// <summary>
    /// Category of the link (Content, Navigation, Footer, External).
    /// </summary>
    public required LinkType Type { get; init; }

    /// <summary>
    /// Importance score (0-100) used to determine initial collapse state.
    /// Higher score = more important = more likely to start expanded.
    /// </summary>
    public required int ImportanceScore { get; init; }

    /// <summary>
    /// ARIA label for accessibility (if present).
    /// </summary>
    public string? AriaLabel { get; init; }

    /// <summary>
    /// CSS selector path from root to link's parent element.
    /// Used for categorization and hierarchy building.
    /// Example: "nav > ul > li"
    /// </summary>
    public string? ParentSelector { get; init; }

    /// <summary>
    /// Author of the linked content (if available from metadata).
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Publication date of the linked content (if available from metadata).
    /// </summary>
    public DateTime? PublishedDate { get; init; }

    /// <summary>
    /// Section heading from the HTML DOM ancestry (e.g., nearest h2/h3 above the link).
    /// Null when no section heading was detected.
    /// </summary>
    public string? SectionTitle { get; init; }

    /// <summary>
    /// True if display text was extracted from an image alt attribute.
    /// Used for deduplication - actual text is preferred over image alt.
    /// </summary>
    public bool IsFromImageAlt { get; init; }

    /// <summary>
    /// True when the link's host differs from the page it was extracted from.
    /// Independent of <see cref="Type"/>: on aggregator sites (Techmeme, HN)
    /// story links are external AND content at the same time.
    /// </summary>
    public bool IsExternal { get; init; }

    /// <summary>
    /// workspace-romy.2: visual geometry measured on the live page before the
    /// HTML snapshot (null for HTTP-fetched or cached pre-geometry HTML).
    /// </summary>
    public LinkGeometry? Geometry { get; init; }

    /// <summary>
    /// workspace-romy.4: true when the link's text or container matched the
    /// ad/sponsor heuristics but the link was story-shaped enough to keep —
    /// surfaced to the AI analyzer (flag=sponsor) and to the wizard's
    /// ordering self-test so promo slots never outrank real stories.
    /// </summary>
    public bool IsSponsored { get; init; }

    /// <summary>
    /// Classifies whether this LinkInfo is a regular link or a header.
    /// </summary>
    public HeaderType HeaderType { get; init; }

    /// <summary>
    /// True if this LinkInfo represents any kind of header (group or sub-section).
    /// Computed from HeaderType for backward compatibility.
    /// </summary>
    public bool IsGroupHeader => HeaderType != HeaderType.None;

    /// <summary>
    /// Determines if this link should start collapsed based on type and importance.
    /// </summary>
    /// <returns>True if should start collapsed, false if expanded.</returns>
    public bool ShouldStartCollapsed()
    {
        // Group headers have their own collapse logic
        if (IsGroupHeader)
        {
            // Content group starts expanded, others collapsed
            return Type != LinkType.Content;
        }

        // Content links with high importance start expanded
        if (Type == LinkType.Content && ImportanceScore >= 50)
            return false;

        // Everything else starts collapsed
        return true;
    }

    /// <summary>
    /// Creates a LinkInfo for a group header.
    /// </summary>
    public static LinkInfo CreateGroupHeader(LinkType type)
    {
        var displayText = type switch
        {
            LinkType.Navigation => "Navigation",
            LinkType.Content => "Content",
            LinkType.External => "External",
            LinkType.Footer => "Footer",
            _ => "Links"
        };

        return new LinkInfo
        {
            Url = string.Empty,
            DisplayText = displayText,
            Type = type,
            ImportanceScore = 100,
            HeaderType = HeaderType.TopLevelGroup
        };
    }

    /// <summary>
    /// Creates a LinkInfo for a sub-section header within a group.
    /// </summary>
    public static LinkInfo CreateSubSectionHeader(string title, LinkType type)
    {
        return new LinkInfo
        {
            Url = string.Empty,
            DisplayText = title,
            Type = type,
            ImportanceScore = 100,
            HeaderType = HeaderType.SubSection
        };
    }
}
