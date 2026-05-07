// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Generates layout candidates (document-order, AI hierarchical, RSS) for a page.
/// Each candidate includes a pre-built NavigationTree for instant preview rendering.
/// </summary>
public interface ILayoutCandidateGenerator
{
    /// <summary>
    /// Generates 1-3 unique layout candidates for a page.
    /// Document-order is always included (instant). AI and RSS are generated
    /// in parallel if available. Duplicates are removed by structural signature.
    /// </summary>
    Task<List<LayoutCandidate>> GenerateCandidatesAsync(
        List<LinkInfo> links,
        string html,
        string pageUrl,
        byte[]? screenshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single layout option with its pre-built tree for preview rendering.
/// </summary>
public sealed record LayoutCandidate
{
    /// <summary>
    /// The hierarchy config that produced this layout (can be saved).
    /// </summary>
    public required SiteHierarchyConfig Config { get; init; }

    /// <summary>
    /// Human-readable summary (e.g., "AI · 5 sections" or "RSS · 47 articles").
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Pre-built navigation tree for instant preview rendering. For unavailable
    /// strategies (workspace-33jw), this is the page's current tree so cycling
    /// to the row shows the existing layout rather than a blank screen.
    /// </summary>
    public required NavigationTree PreviewTree { get; init; }

    /// <summary>
    /// True when this row represents a strategy the user cannot currently apply
    /// (e.g., AI Curated without an OpenAI API key). Surfaced so the chooser
    /// can render the row as disabled and refuse to save it. workspace-33jw.
    /// </summary>
    public bool IsUnavailable { get; init; }

    /// <summary>
    /// Human-readable reason this strategy is unavailable, surfaced in the
    /// chooser status bar so the user knows what to do (e.g., "No OpenAI
    /// API key — press S to open Setup"). Null when <see cref="IsUnavailable"/>
    /// is false. workspace-33jw.
    /// </summary>
    public string? UnavailableReason { get; init; }
}
