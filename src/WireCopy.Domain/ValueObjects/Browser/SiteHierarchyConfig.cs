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
}

public record HierarchySection
{
    public required string Name { get; init; }
    public required int SortOrder { get; init; }
    public List<string> ParentSelectors { get; init; } = new();
    public List<string> UrlPatterns { get; init; } = new();
    public bool StartCollapsed { get; init; }
}
