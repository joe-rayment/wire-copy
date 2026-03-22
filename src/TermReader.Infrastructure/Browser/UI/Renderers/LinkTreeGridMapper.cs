// Educational and personal use only.

using TermReader.Domain.Entities.Browser;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Stateless utility that maps a flat visible-node list into a 2D grid structure.
/// Group headers always get their own full-width row.
/// Regular links are paired left-right within each section.
/// </summary>
internal static class LinkTreeGridMapper
{
    /// <summary>
    /// Maps visible nodes into grid rows. Group headers get full-width rows.
    /// Regular links are paired left-right (within each section between headers).
    /// </summary>
    public static List<GridRow> MapToGrid(List<LinkNode> visibleNodes, int columns)
    {
        var rows = new List<GridRow>();

        if (columns <= 1)
        {
            // Single column: each node is its own row
            for (var i = 0; i < visibleNodes.Count; i++)
            {
                rows.Add(new GridRow(visibleNodes[i], null, visibleNodes[i].IsGroupHeader, i));
            }

            return rows;
        }

        // Two-column mode: group headers get full width, links are paired within sections
        var i2 = 0;
        while (i2 < visibleNodes.Count)
        {
            var node = visibleNodes[i2];

            if (node.IsGroupHeader)
            {
                rows.Add(new GridRow(node, null, true, i2));
                i2++;
                continue;
            }

            // Regular link: pair with next non-header node if available
            LinkNode? rightNode = null;
            var nextIndex = i2 + 1;
            if (nextIndex < visibleNodes.Count && !visibleNodes[nextIndex].IsGroupHeader)
            {
                rightNode = visibleNodes[nextIndex];
                i2 = nextIndex + 1;
            }
            else
            {
                i2++;
            }

            rows.Add(new GridRow(node, rightNode, false, 0));
        }

        // Fix StartNodeIndex to track actual visible node indices
        var nodeIdx = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            rows[r] = rows[r] with { StartNodeIndex = nodeIdx };
            if (rows[r].IsGroupHeader)
            {
                nodeIdx++;
            }
            else
            {
                nodeIdx += rows[r].Right != null ? 2 : 1;
            }
        }

        return rows;
    }

    /// <summary>
    /// Converts a flat node index to a (row, col) grid position.
    /// </summary>
    public static (int Row, int Col) NodeIndexToGridPosition(List<GridRow> gridRows, int nodeIndex)
    {
        for (var row = 0; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];

            if (gr.IsGroupHeader)
            {
                if (gr.StartNodeIndex == nodeIndex)
                {
                    return (row, 0);
                }

                continue;
            }

            if (gr.StartNodeIndex == nodeIndex)
            {
                return (row, 0);
            }

            if (gr.Right != null && gr.StartNodeIndex + 1 == nodeIndex)
            {
                return (row, 1);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// Converts a (row, col) grid position to a flat node index.
    /// </summary>
    public static int GridPositionToNodeIndex(List<GridRow> gridRows, int row, int col)
    {
        if (row < 0 || row >= gridRows.Count)
        {
            return 0;
        }

        var gr = gridRows[row];

        if (gr.IsGroupHeader || col == 0)
        {
            return gr.StartNodeIndex;
        }

        // Column 1 (right side)
        return gr.Right != null ? gr.StartNodeIndex + 1 : gr.StartNodeIndex;
    }

    /// <summary>
    /// Moves down from the current position, preserving column across group headers.
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

        // Preserve column; if right is null and col==1, fall back to left
        var col = currentCol;
        if (col == 1 && gr.Right == null)
        {
            col = 0;
        }

        return GridPositionToNodeIndex(gridRows, nextRow, col);
    }

    /// <summary>
    /// Moves up from the current position, preserving column across group headers.
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

        var col = currentCol;
        if (col == 1 && gr.Right == null)
        {
            col = 0;
        }

        return GridPositionToNodeIndex(gridRows, prevRow, col);
    }
}
