// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// One row of the link-tree card grid. Holds 1..N cells (workspace-ehon — was a
/// fixed <c>Left</c>/<c>Right</c> pair). Group-header rows always hold exactly one
/// full-width cell; regular rows hold up to <c>columns</c> link cells, with the
/// last row of a section possibly fewer. <see cref="StartNodeIndex"/> is the index
/// of the row's first cell in the flat visible-node list, so the flat index of
/// cell <c>c</c> is <c>StartNodeIndex + c</c>.
/// </summary>
internal record GridRow(IReadOnlyList<LinkNode> Cells, bool IsGroupHeader, int StartNodeIndex)
{
    /// <summary>
    /// The row's first (representative) cell — the header node, or the row's
    /// leading link. Retained as a convenience for the many call sites that only
    /// need the leading node (header render, row height, section selection).
    /// </summary>
    public LinkNode Left => Cells[0];

    /// <summary>
    /// The second cell when the row has one; null otherwise. Legacy two-column
    /// convenience kept so existing mapper tests read unchanged.
    /// </summary>
    public LinkNode? Right => Cells.Count > 1 ? Cells[1] : null;
}
