// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Stateless utility that maps a flat visible-node list into a 2D grid structure.
/// Group headers always get their own full-width row. Regular links fill
/// left-to-right across up to <c>columns</c> cells WITHIN each section between
/// headers (workspace-ehon — was a fixed left/right pair).
/// </summary>
internal static class LinkTreeGridMapper
{
    /// <summary>
    /// Maps visible nodes into grid rows. Group headers get full-width single-cell
    /// rows. Regular links are chunked into runs of <paramref name="columns"/>
    /// cells within each section; the last row of a section may hold fewer cells.
    /// </summary>
    public static List<GridRow> MapToGrid(List<LinkNode> visibleNodes, int columns)
    {
        var cols = Math.Max(1, columns);
        var rows = new List<GridRow>();

        var i = 0;
        while (i < visibleNodes.Count)
        {
            var node = visibleNodes[i];

            // Group header: its own full-width row. Advancing by 1 also stops a
            // link chunk from spanning a section boundary.
            if (node.IsGroupHeader)
            {
                rows.Add(new GridRow(new[] { node }, true, i));
                i++;
                continue;
            }

            // Consume a run of consecutive non-header nodes, up to `cols` per row.
            var chunk = new List<LinkNode> { node };
            var j = i + 1;
            while (chunk.Count < cols && j < visibleNodes.Count && !visibleNodes[j].IsGroupHeader)
            {
                chunk.Add(visibleNodes[j]);
                j++;
            }

            // StartNodeIndex is the flat index of the row's first cell, which
            // equals the count of nodes consumed before it — so cell c of this
            // row is flat node index i + c.
            rows.Add(new GridRow(chunk, false, i));
            i = j;
        }

        return rows;
    }

    /// <summary>
    /// Converts a flat node index to a (row, col) grid position. Col is the
    /// node's offset within its row's cells.
    /// </summary>
    public static (int Row, int Col) NodeIndexToGridPosition(List<GridRow> gridRows, int nodeIndex)
    {
        for (var row = 0; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];
            var offset = nodeIndex - gr.StartNodeIndex;
            if (offset >= 0 && offset < gr.Cells.Count)
            {
                return (row, offset);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// Converts a (row, col) grid position to a flat node index. An out-of-range
    /// col clamps to the row's last cell.
    /// </summary>
    public static int GridPositionToNodeIndex(List<GridRow> gridRows, int row, int col)
    {
        if (row < 0 || row >= gridRows.Count)
        {
            return 0;
        }

        var gr = gridRows[row];
        var c = Math.Clamp(col, 0, gr.Cells.Count - 1);
        return gr.StartNodeIndex + c;
    }

    /// <summary>
    /// Moves down from the current position, preserving column across group
    /// headers and clamping to a narrower next row's cell count.
    /// </summary>
    public static int MoveDown(List<GridRow> gridRows, int currentRow, int currentCol)
    {
        var nextRow = currentRow + 1;
        if (nextRow >= gridRows.Count)
        {
            return GridPositionToNodeIndex(gridRows, currentRow, currentCol);
        }

        var gr = gridRows[nextRow];
        if (gr.IsGroupHeader)
        {
            return GridPositionToNodeIndex(gridRows, nextRow, 0);
        }

        var col = Math.Min(currentCol, gr.Cells.Count - 1);
        return GridPositionToNodeIndex(gridRows, nextRow, col);
    }

    /// <summary>
    /// Moves up from the current position, preserving column across group headers
    /// and clamping to a narrower previous row's cell count.
    /// </summary>
    public static int MoveUp(List<GridRow> gridRows, int currentRow, int currentCol)
    {
        var prevRow = currentRow - 1;
        if (prevRow < 0)
        {
            return GridPositionToNodeIndex(gridRows, currentRow, currentCol);
        }

        var gr = gridRows[prevRow];
        if (gr.IsGroupHeader)
        {
            return GridPositionToNodeIndex(gridRows, prevRow, 0);
        }

        var col = Math.Min(currentCol, gr.Cells.Count - 1);
        return GridPositionToNodeIndex(gridRows, prevRow, col);
    }
}
