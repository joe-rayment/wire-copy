// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// Serialisable view-model of the link-tree (story list) screen, exported over
/// the shell channel (method <c>linkTreeState</c>) so the desktop shell can
/// render the link list as an HTML pane ("Link Tree Responsive.dc.html",
/// workspace-vingi). The items are EXACTLY the grid-row window the TUI render
/// admitted — same <c>LinkTreeGridMapper</c> rows, same scroll offset, same
/// line-budget admission — so the pane can never drift from the terminal grid.
/// <see cref="Columns"/> is the pane's responsive track count, which shell-mode
/// story navigation also uses, so arrow keys always match the visible grid.
/// Field names are camelCase on the wire.
/// </summary>
public sealed record LinkTreeViewState
{
    /// <summary>Page title for the CRT-glow header.</summary>
    public required string Title { get; init; }

    /// <summary>Host of the page URL (header meta line); null when unparseable.</summary>
    public string? Domain { get; init; }

    /// <summary>Total links on the page (header meta line).</summary>
    public int LinkCount { get; init; }

    /// <summary>Top-level section count (header meta line; hidden when &lt; 2).</summary>
    public int SectionCount { get; init; }

    /// <summary>True when the current page itself is in the offline cache.</summary>
    public bool Cached { get; init; }

    /// <summary>Responsive grid track count (1..6) — also the arrow-key geometry.</summary>
    public int Columns { get; init; }

    /// <summary>The admitted window: section headers and story cards in grid order.</summary>
    public required IReadOnlyList<LinkTreeItem> Items { get; init; }

    /// <summary>Index into <see cref="Items"/> of the selected node; -1 when none is visible.</summary>
    public int SelectedIndex { get; init; }

    /// <summary>Grid rows scrolled above the window (the ▲ indicator count).</summary>
    public int MoreAbove { get; init; }

    /// <summary>Grid rows below the window (the ▼ indicator count).</summary>
    public int MoreBelow { get; init; }

    /// <summary>Story cards inside the window (the "N of M shown" readout).</summary>
    public int ShownStories { get; init; }

    /// <summary>Story cards in the whole visible tree.</summary>
    public int TotalStories { get; init; }

    /// <summary>Multi-select size (0 = no selection set active).</summary>
    public int SelectedCount { get; init; }

    /// <summary>Pre-load cache meter for the footer; null when no progress to show.</summary>
    public LinkTreeCacheMeter? Cache { get; init; }

    /// <summary>True when the browser sidecar is docked (the footer's ⇉ marker).</summary>
    public bool Docked { get; init; }
}

/// <summary>
/// One window entry: a full-width section header (<c>kind = "section"</c>) or a
/// story card (<c>kind = "story"</c>).
/// </summary>
public sealed record LinkTreeItem
{
    /// <summary>"section" or "story".</summary>
    public required string Kind { get; init; }

    /// <summary>Section name or story headline, as the TUI renders it.</summary>
    public required string Title { get; init; }

    /// <summary>Story href (used by gates / never displayed); null for sections.</summary>
    public string? Url { get; init; }

    /// <summary>Story metadata line ("Author · date"); null/empty when unknown.</summary>
    public string? Meta { get; init; }

    /// <summary>Story link type: "content", "navigation", "external", or "footer".</summary>
    public string? LinkType { get; init; }

    /// <summary>Section child count (the "(n)" suffix).</summary>
    public int Count { get; init; }

    /// <summary>True when a section is collapsed (▶ instead of ▼).</summary>
    public bool Collapsed { get; init; }

    /// <summary>Section multi-select marker: "full" (✓), "partial" (◐), or null.</summary>
    public string? SelectState { get; init; }

    /// <summary>True when a story is in the multi-select set (✓ on the card).</summary>
    public bool Toggled { get; init; }
}

/// <summary>Footer cache meter: how many eligible links are pre-loaded.</summary>
public sealed record LinkTreeCacheMeter
{
    public int Done { get; init; }

    public int Total { get; init; }
}
