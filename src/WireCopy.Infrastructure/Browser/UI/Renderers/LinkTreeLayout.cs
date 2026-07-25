// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared layout parameters for the link tree 2-column grid view.
/// </summary>
/// <param name="PaneBudgetLines">
/// Ceiling on the lines the row-admission loop may spend, so the admitted window
/// never exceeds what the HTML pane can DRAW (workspace-h2caq). The pane spends far
/// more of its height on chrome — CRT masthead, rules, footer — than the terminal's
/// five lines, and "Link Tree.dc.html" cards are up to 242px tall, so without this
/// the TUI admits a row the pane slices in half at its clipping edge. Terminal-mode
/// layouts leave it at <see cref="int.MaxValue"/>: the caller's budget already rules.
/// </param>
internal record LinkTreeLayout(
    int Width,
    int Columns,
    int CellHeight,
    int CellWidth,
    int VisibleRows,
    int HeaderLines,
    int StatusBarLines,
    int PaneBudgetLines = int.MaxValue);
