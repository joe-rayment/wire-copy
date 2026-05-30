// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// A single layout option with its pre-built tree for preview rendering. Used
/// by the strategy chooser's preview carousel.
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
    /// API key — press c on the launcher to open Setup"). Null when <see cref="IsUnavailable"/>
    /// is false. workspace-33jw.
    /// </summary>
    public string? UnavailableReason { get; init; }
}
