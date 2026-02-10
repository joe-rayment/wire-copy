// Educational and personal use only.

using NYTAudioScraper.Domain.Enums.Browser;

namespace NYTAudioScraper.Domain.ValueObjects.Browser;

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
    /// True if display text was extracted from an image alt attribute.
    /// Used for deduplication - actual text is preferred over image alt.
    /// </summary>
    public bool IsFromImageAlt { get; init; }

    /// <summary>
    /// True if this LinkInfo represents a group header (e.g., "Navigation", "Content").
    /// Group headers have special rendering and behavior (toggle collapse on Enter).
    /// </summary>
    public bool IsGroupHeader { get; init; }

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
    public static LinkInfo CreateGroupHeader(LinkType type, int linkCount)
    {
        var displayText = type switch
        {
            LinkType.Navigation => $"Navigation ({linkCount})",
            LinkType.Content => $"Content ({linkCount})",
            LinkType.External => $"External ({linkCount})",
            LinkType.Footer => $"Footer ({linkCount})",
            _ => $"Links ({linkCount})"
        };

        return new LinkInfo
        {
            Url = string.Empty,
            DisplayText = displayText,
            Type = type,
            ImportanceScore = 100,
            IsGroupHeader = true
        };
    }
}
