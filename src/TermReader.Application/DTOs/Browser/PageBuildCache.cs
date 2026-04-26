// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
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

    public PageClassification Classification { get; init; } = PageClassification.Unknown;

    /// <summary>
    /// Version of the classification logic used when this cache was built.
    /// Caches with a stale version are rejected to prevent misclassification.
    /// </summary>
    public int ClassificationVersion { get; init; }

    /// <summary>
    /// Raw score from the signal-scored classifier. Positive = article, negative = link list.
    /// Stored for debugging when classification is wrong.
    /// </summary>
    public int ClassificationScore { get; init; }

    /// <summary>
    /// RSS/Atom feeds discovered on the page. Preserved in cache so feed
    /// detection does not need to be repeated on cache hits.
    /// </summary>
    public List<FeedInfo>? DetectedFeeds { get; init; }

    public DateTime CachedAt { get; init; } = DateTime.UtcNow;
}
