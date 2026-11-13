// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Selection movement over a <see cref="TileScreen"/> (workspace-6ifpp.1). Selectable items are numbered
/// in cell order, a pair's top half before its bottom half; <see cref="Bar"/> (-1) is the bar. The pane
/// lays the cells out in <see cref="TileScreen.Columns"/> tracks, so moving by cell here matches what is lit.
/// </summary>
internal static class TileGrid
{
    public const int Bar = -1;

    private const int Whole = 0;
    private const int Top = 1;
    private const int Bottom = 2;

    /// <summary>Every selectable item, in selection order.</summary>
    public static IReadOnlyList<Tile> Items(TileScreen screen)
    {
        var items = new List<Tile>();
        foreach (var cell in screen.Cells)
        {
            if (cell.Tile != null)
            {
                items.Add(cell.Tile);
                continue;
            }

            if (cell.Top != null)
            {
                items.Add(cell.Top);
            }

            if (cell.Bottom != null)
            {
                items.Add(cell.Bottom);
            }
        }

        return items;
    }

    /// <summary>The item at <paramref name="selected"/>; null for the bar or an index past the end.</summary>
    public static Tile? ItemAt(TileScreen screen, int selected)
    {
        var items = Items(screen);
        return selected >= 0 && selected < items.Count ? items[selected] : null;
    }

    /// <summary>
    /// Where the item with <paramref name="id"/> sits now; when it is gone, <paramref name="fallback"/>
    /// clamped to what exists (the bar stays the bar only if the screen has one).
    /// </summary>
    public static int IndexOfId(TileScreen screen, string? id, int fallback)
    {
        var items = Items(screen);
        if (id != null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Id == id)
                {
                    return i;
                }
            }
        }

        if (fallback < 0 && screen.BarPlaceholder != null)
        {
            return Bar;
        }

        if (items.Count == 0)
        {
            return screen.BarPlaceholder != null ? Bar : 0;
        }

        return Math.Clamp(fallback, 0, items.Count - 1);
    }

    /// <summary>Digit jump: "4" selects the fourth item; a digit with no item changes nothing.</summary>
    public static int Jump(TileScreen screen, int digit)
    {
        return digit >= 1 && digit <= Items(screen).Count ? digit - 1 : screen.Selected;
    }

    /// <summary>The selection after one arrow key (hjkl arrive as the same command types).</summary>
    public static int Move(TileScreen screen, CommandType type)
    {
        var positions = Positions(screen);
        var hasBar = screen.BarPlaceholder != null;
        var selected = screen.Selected;
        if (positions.Count == 0)
        {
            return hasBar ? Bar : selected;
        }

        if (selected < 0)
        {
            return type == CommandType.MoveDown || !hasBar ? 0 : Bar;
        }

        selected = Math.Min(selected, positions.Count - 1);
        var (cell, part) = positions[selected];
        var cols = Math.Max(1, screen.Columns);
        switch (type)
        {
            case CommandType.MoveDown:
                if (part == Top && Find(positions, cell, Bottom) is var below and >= 0)
                {
                    return below;
                }

                for (var c = cell + cols; c < screen.Cells.Count; c += cols)
                {
                    if (Enter(positions, c, Top) is var hit and >= 0)
                    {
                        return hit;
                    }
                }

                return selected;

            case CommandType.MoveUp:
                if (part == Bottom && Find(positions, cell, Top) is var above and >= 0)
                {
                    return above;
                }

                for (var c = cell - cols; c >= 0; c -= cols)
                {
                    if (Enter(positions, c, Bottom) is var hit and >= 0)
                    {
                        return hit;
                    }
                }

                return hasBar ? Bar : selected;

            case CommandType.MoveRight:
            case CommandType.ExpandNode:
                if (cell % cols == cols - 1)
                {
                    return selected;
                }

                return Enter(positions, cell + 1, part) is var right and >= 0 ? right : selected;

            case CommandType.MoveLeft:
            case CommandType.CollapseNode:
                if (cell % cols == 0)
                {
                    return selected;
                }

                return Enter(positions, cell - 1, part) is var left and >= 0 ? left : selected;

            default:
                return selected;
        }
    }

    private static List<(int Cell, int Part)> Positions(TileScreen screen)
    {
        var positions = new List<(int Cell, int Part)>();
        for (var c = 0; c < screen.Cells.Count; c++)
        {
            var cell = screen.Cells[c];
            if (cell.Tile != null)
            {
                positions.Add((c, Whole));
                continue;
            }

            if (cell.Top != null)
            {
                positions.Add((c, Top));
            }

            if (cell.Bottom != null)
            {
                positions.Add((c, Bottom));
            }
        }

        return positions;
    }

    private static int Find(List<(int Cell, int Part)> positions, int cell, int part)
    {
        return positions.FindIndex(p => p.Cell == cell && p.Part == part);
    }

    /// <summary>The item to land on in <paramref name="cell"/>: its whole tile, else the preferred half, else the other half; -1 when the cell is empty.</summary>
    private static int Enter(List<(int Cell, int Part)> positions, int cell, int preferPart)
    {
        var whole = Find(positions, cell, Whole);
        if (whole >= 0)
        {
            return whole;
        }

        var first = preferPart == Bottom ? Bottom : Top;
        var second = preferPart == Bottom ? Top : Bottom;
        var preferred = Find(positions, cell, first);
        return preferred >= 0 ? preferred : Find(positions, cell, second);
    }
}
