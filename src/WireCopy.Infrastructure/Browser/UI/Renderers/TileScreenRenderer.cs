// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>Builds the launcher-pane view state for a <see cref="TileScreen"/> (workspace-6ifpp.1).</summary>
internal static class TileScreenRenderer
{
    internal static LauncherViewState BuildViewState(TileScreen screen, RenderOptions options)
    {
        var columns = Math.Max(1, screen.Columns);
        var pageSize = Math.Max(1, columns * (screen.RowsPerPage ?? LauncherRenderer.PaneRowsPerPage(options)));

        // Item number of the first item in each cell, and the cell holding the selection.
        var firstItem = new int[screen.Cells.Count];
        var running = 0;
        var selectedCell = -1;
        for (var c = 0; c < screen.Cells.Count; c++)
        {
            firstItem[c] = running;
            var cell = screen.Cells[c];
            var halves = (cell.Top != null ? 1 : 0) + (cell.Bottom != null ? 1 : 0);
            var count = cell.Tile != null ? 1 : halves;
            if (screen.Selected >= running && screen.Selected < running + count)
            {
                selectedCell = c;
            }

            running += count;
        }

        var total = running;
        var page = selectedCell >= 0 ? selectedCell / pageSize : 0;
        var start = page * pageSize;
        var end = Math.Min(screen.Cells.Count, start + pageSize);

        var slots = new List<LauncherSlot>();
        var selectedSlot = -1;
        var emitted = 0;
        for (var c = start; c < end; c++)
        {
            var cell = screen.Cells[c];
            var index = firstItem[c];
            if (cell.Tile != null)
            {
                if (index == screen.Selected)
                {
                    selectedSlot = slots.Count;
                }

                slots.Add(Slot(cell.Tile, index, "routine", null, null));
                emitted++;
            }
            else if (cell.Top != null || cell.Bottom != null)
            {
                if (cell.Top != null)
                {
                    if (index == screen.Selected)
                    {
                        selectedSlot = slots.Count;
                    }

                    slots.Add(Slot(cell.Top, index++, "routines", "top", cell.Legend));
                    emitted++;
                }

                if (cell.Bottom != null)
                {
                    if (index == screen.Selected)
                    {
                        selectedSlot = slots.Count;
                    }

                    slots.Add(Slot(cell.Bottom, index, "routines", "bottom", null));
                    emitted++;
                }
            }
            else
            {
                slots.Add(new LauncherSlot { Index = index, Kind = "empty" });
            }
        }

        var cellsShown = end - start;
        var pad = (((cellsShown + columns - 1) / columns) * columns) - cellsShown;
        for (var k = 0; k < pad; k++)
        {
            slots.Add(new LauncherSlot { Index = total, Kind = "empty" });
        }

        var offset = start < firstItem.Length ? firstItem[start] : total;
        return new LauncherViewState
        {
            Screen = screen.Screen,
            Mode = options.UiMode,
            ModeVariant = options.UiModeVariant,
            Eyebrow = screen.Eyebrow,
            Title = screen.Title,
            Tagline = screen.Tagline,
            Version = string.Empty,
            CountLabel = screen.CountLabel,
            FooterCount = screen.FooterCount,
            Footer = new LauncherFooter { Badge = screen.Badge, Status = screen.Status },
            BarHidden = screen.BarPlaceholder == null,
            BarPlaceholder = screen.BarPlaceholder,
            UrlTyping = screen.BarTyping,
            SetupHint = screen.HintRow,
            AppLabel = screen.AppLabel,
            Hints = screen.Hints,
            PromptLine = screen.Confirm,
            Columns = columns,
            BookmarkCount = 0,
            Slots = slots,
            SelectedIndex = screen.Selected < 0 ? -1 : selectedSlot,
            Paging = new LauncherPaging { Offset = offset, Visible = emitted, Total = total },
        };
    }

    private static LauncherSlot Slot(Tile tile, int index, string kind, string? half, string? legend) => new()
    {
        Index = index,
        Kind = kind,
        Half = half,
        Legend = legend,
        Title = tile.Title,
        Hint = tile.Hint,
        Detail = tile.Detail,
        Alert = tile.Alert,
        Disabled = tile.Disabled,
        Badge = index < 9 ? (index + 1).ToString() : null,
    };
}
