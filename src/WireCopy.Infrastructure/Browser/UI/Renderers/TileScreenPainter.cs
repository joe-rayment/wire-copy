// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>Plain-terminal (ANSI) drawing of a <see cref="TileScreen"/> (workspace-6ifpp.1).</summary>
internal static class TileScreenPainter
{
    private const string Bold = "\x1b[1m";
    private const string Reset = "\x1b[0m";
    private const int Gap = 2;
    private const int FooterLines = 2;

    /// <summary>0-based terminal row and column where text typed into the bar starts (matches <see cref="Paint"/>).</summary>
    internal static (int Row, int Col) BarInputPosition(TileScreen screen, RenderOptions options)
    {
        // Row 0 blank, 1 eyebrow, 2 title, 3 tagline, 4 blank, 5 bar; column = margin + " " + rail + " ".
        var margin = LauncherRenderer.ColumnLeftMargin(options.TerminalWidth);
        return (5, margin + 3);
    }

    internal static void Paint(RenderHelpers helpers, ThemePalette palette, TileScreen screen, RenderOptions options)
    {
        var width = options.TerminalWidth;
        var contentWidth = LauncherRenderer.ContentWidthFor(width);
        var margin = new string(' ', LauncherRenderer.ColumnLeftMargin(width));
        var inner = Math.Max(4, contentWidth - 1);
        var accent = palette.GetRoutineAccentFg().AnsiFg;
        var muted = palette.GetMutedFg().AnsiFg;
        var dim = palette.GetDimFg().AnsiFg;
        var primary = palette.PrimaryText.AnsiFg;
        var error = palette.ErrorFg.AnsiFg;

        helpers.Clear();
        helpers.WriteLine(string.Empty);
        var headerLines = 1;

        string Split(string left, string leftStyle, string right, string rightStyle)
        {
            var r = RenderHelpers.TruncateText(right, Math.Max(0, inner - 2));
            var l = RenderHelpers.TruncateText(left, Math.Max(1, inner - r.Length - (r.Length > 0 ? 1 : 0)));
            var pad = Math.Max(r.Length > 0 ? 1 : 0, inner - l.Length - r.Length);
            var rightPart = r.Length > 0 ? $"{rightStyle}{r}{Reset}" : string.Empty;
            return $"{margin} {leftStyle}{l}{Reset}{new string(' ', pad)}{rightPart}";
        }

        helpers.WriteLine(Split($"▌ {screen.Eyebrow.ToUpperInvariant()}", accent, screen.CountLabel, muted));
        var headRight = screen.Badge ?? screen.Status ?? string.Empty;
        helpers.WriteLine(Split(screen.Title, primary + Bold, headRight, screen.Badge != null ? error : muted));
        helpers.WriteLine($"{margin} {muted}{RenderHelpers.TruncateText(screen.Tagline, inner)}{Reset}");
        helpers.WriteLine(string.Empty);
        headerLines += 4;

        if (screen.BarPlaceholder != null)
        {
            var typing = screen.BarTyping != null;
            var text = RenderHelpers.TruncateText(typing ? screen.BarTyping!.Buffer : screen.BarPlaceholder, Math.Max(1, inner - 3));
            var rail = screen.Selected == -1 ? $"{accent}▌{Reset}" : $"{dim}│{Reset}";
            var fg = typing ? primary : dim;
            helpers.WriteLine($"{margin} {rail} {fg}{text}{Reset}");
            headerLines++;
        }

        if (screen.HintRow != null)
        {
            helpers.WriteLine($"{margin} {muted}{RenderHelpers.TruncateText(screen.HintRow, inner)}{Reset}");
            helpers.WriteLine(string.Empty);
            headerLines += 2;
        }

        // Number the selectable items in cell order (Top before Bottom).
        var next = 0;
        Item? Make(Tile? t)
        {
            if (t == null)
            {
                return null;
            }

            var i = next++;
            return new Item(t, i + 1, i == screen.Selected);
        }

        var cols = Math.Max(1, screen.Columns);
        var cells = screen.Cells.Select(c => (Cell: c, Whole: Make(c.Tile), Top: Make(c.Top), Bottom: Make(c.Bottom))).ToList();
        var totalRows = (cells.Count + cols - 1) / cols;
        var selRow = cells.FindIndex(c => (c.Whole?.Selected ?? false) || (c.Top?.Selected ?? false) || (c.Bottom?.Selected ?? false));
        selRow = selRow < 0 ? 0 : selRow / cols;

        var avail = Math.Max(5, options.TerminalHeight - headerLines - FooterLines - 1);
        var rowsFit = Math.Max(1, avail / 5);
        if (totalRows > rowsFit)
        {
            rowsFit = Math.Max(1, (avail - 2) / 5);
        }

        var startRow = selRow < rowsFit ? 0 : Math.Min(selRow - rowsFit + 1, Math.Max(0, totalRows - rowsFit));
        var endRow = Math.Min(totalRows, startRow + rowsFit);
        var cellWidth = Math.Max(8, (contentWidth - (Gap * (cols - 1))) / cols);
        var textWidth = Math.Max(4, cellWidth - 2);
        var used = headerLines;

        if (startRow > 0)
        {
            helpers.WriteLine($"{margin}  {dim}▲ more{Reset}");
            used++;
        }

        // One cell line: rail, left text (+ right-aligned text or dim legend), padded to the cell width.
        string Line(Item? item, string left, string leftStyle, string right, string legend)
        {
            if (item == null)
            {
                return new string(' ', cellWidth);
            }

            var rail = item.Selected ? $"{accent}▌{Reset} " : "  ";
            var style = leftStyle;
            if (item.Tile.Disabled)
            {
                style = dim;
            }
            else if (item.Selected)
            {
                style = primary;
            }

            var l = RenderHelpers.TruncateText(left, textWidth);
            var sb = new StringBuilder(rail);
            if (right.Length > 0 && l.Length + 1 + right.Length <= textWidth)
            {
                sb.Append($"{style}{l}{Reset}{new string(' ', textWidth - l.Length - right.Length)}{style}{right}{Reset}");
                return sb.ToString();
            }

            var visible = l.Length;
            sb.Append($"{style}{l}{Reset}");
            if (legend.Length > 0 && visible + legend.Length <= textWidth)
            {
                sb.Append($"{dim}{legend}{Reset}");
                visible += legend.Length;
            }

            sb.Append(' ', Math.Max(0, textWidth - visible));
            return sb.ToString();
        }

        static string Num(Item? i) => i != null && i.Number <= 9 ? $"[{i.Number}]" : string.Empty;
        string PairTitle(Item? i) => i == null ? string.Empty : $"{i.Tile.Title.ToUpperInvariant()} {Num(i)}".TrimEnd();

        for (var r = startRow; r < endRow; r++)
        {
            var row = cells.Skip(r * cols).Take(cols).ToList();
            var lines = new List<string>[4];
            for (var k = 0; k < 4; k++)
            {
                lines[k] = [];
            }

            foreach (var c in row)
            {
                if (c.Whole != null)
                {
                    var t = c.Whole.Tile;
                    lines[0].Add(Line(c.Whole, t.Title.ToUpperInvariant(), primary + Bold, Num(c.Whole), string.Empty));
                    lines[1].Add(Line(c.Whole, t.Hint, muted, string.Empty, string.Empty));
                    lines[2].Add(Line(c.Whole, t.Detail ?? string.Empty, muted, string.Empty, string.Empty));
                    lines[3].Add(Line(c.Whole, t.Alert ?? string.Empty, t.Disabled ? dim : error, string.Empty, string.Empty));
                }
                else if (c.Top != null || c.Bottom != null)
                {
                    var legend = string.IsNullOrEmpty(c.Cell.Legend) ? string.Empty : $"  · {c.Cell.Legend}";
                    lines[0].Add(Line(c.Top, PairTitle(c.Top), primary + Bold, string.Empty, legend));
                    lines[1].Add(Line(c.Top, c.Top?.Tile.Hint ?? string.Empty, muted, string.Empty, string.Empty));
                    lines[2].Add(Line(c.Bottom, PairTitle(c.Bottom), primary + Bold, string.Empty, string.Empty));
                    lines[3].Add(Line(c.Bottom, c.Bottom?.Tile.Hint ?? string.Empty, muted, string.Empty, string.Empty));
                }
                else
                {
                    for (var k = 0; k < 4; k++)
                    {
                        lines[k].Add(new string(' ', cellWidth));
                    }
                }
            }

            for (var k = 0; k < 4; k++)
            {
                helpers.WriteLine(margin + string.Join(new string(' ', Gap), lines[k]));
            }

            helpers.WriteLine(string.Empty);
            used += 5;
        }

        if (endRow < totalRows)
        {
            helpers.WriteLine($"{margin}  {dim}▼ more{Reset}");
            used++;
        }

        // Footer pinned to the bottom.
        for (var i = used; i < options.TerminalHeight - FooterLines - 1; i++)
        {
            helpers.WriteLine(string.Empty);
        }

        helpers.WriteLine($"{margin}{dim}{new string('─', contentWidth)}{Reset}");
        if (screen.Confirm != null)
        {
            helpers.WriteLine($"{margin} {primary}{RenderHelpers.TruncateText(screen.Confirm.Prefix + screen.Confirm.Buffer, inner)}{Reset}");
            return;
        }

        var count = RenderHelpers.TruncateText(screen.FooterCount, Math.Max(0, inner / 3));
        var budget = inner - count.Length - (count.Length > 0 ? 2 : 0);
        var plain = new StringBuilder();
        var styled = new StringBuilder();
        var label = RenderHelpers.TruncateText(screen.AppLabel, Math.Max(1, budget));
        plain.Append(label);
        styled.Append($"{accent}{label}{Reset}");
        foreach (var h in screen.Hints)
        {
            var seg = $"[{h.Key}]:{h.Label}";
            if (plain.Length + 2 + seg.Length > budget)
            {
                break;
            }

            plain.Append("  ").Append(seg);
            styled.Append($"  {accent}[{h.Key}]{Reset}{muted}:{h.Label}{Reset}");
        }

        var gapFill = Math.Max(count.Length > 0 ? 1 : 0, inner - plain.Length - count.Length);
        var countPart = count.Length > 0 ? $"{muted}{count}{Reset}" : string.Empty;
        helpers.WriteLine($"{margin} {styled}{new string(' ', gapFill)}{countPart}");
    }

    private sealed record Item(Tile Tile, int Number, bool Selected);
}
