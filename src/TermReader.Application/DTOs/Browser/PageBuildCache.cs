// Educational and personal use only.

using TermReader.Domain.Entities.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Cached intermediate results from page building: extracted links,
/// hierarchy config, and readable content. Allows rebuilding a fresh
/// NavigationTree (with clean selection state) without re-parsing HTML
/// or re-running AI hierarchy analysis.
/// </summary>
public sealed record PageBuildCache
{
    public required List<LinkInfo> Links { get; init; }

    public SiteHierarchyConfig? HierarchyConfig { get; init; }

    public ReadableContent? ReadableContent { get; init; }

    public required PageMetadata Metadata { get; init; }

    public required string FinalUrl { get; init; }

    public DateTime CachedAt { get; init; } = DateTime.UtcNow;
}
