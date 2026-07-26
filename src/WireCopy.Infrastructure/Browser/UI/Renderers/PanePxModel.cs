// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Geometry shared by the projected HTML panes (launcher and link tree), in the
/// panes' own design px.
/// </summary>
/// <remarks>
/// The px-mirror contract (shell/README.md) splits the work: C# owns HOW MANY rows a
/// page holds, the stylesheet owns HOW TALL each one is. This class holds the C# half
/// of the arithmetic that both panes share, so the two cannot drift apart the way their
/// independently-derived cell/line/chrome constants did.
/// </remarks>
internal static class PanePxModel
{
    /// <summary>
    /// One terminal cell in HTML-pane design px, at the 18px scale, as the LINK-TREE pane
    /// measures it (gates/probe-pane-px.mjs, 2026-07-26: 69 cols x 41 rows over a real
    /// 789 x 900px pane). CSS zoom cancels out of both ratios, which is why the panes'
    /// clamp()s can be mirrored in cell units at all.
    /// </summary>
    /// <remarks>
    /// workspace-vt6o7 set out to make ONE pair serve both panes and found it cannot yet:
    /// see LauncherRenderer's own constants for the measurement and the regression it
    /// caused. These ratios absorb term.html's 24px of padding and the sub-row remainder
    /// into the cell size over whichever region the pane occupies, so they are not
    /// interchangeable until the pane-height model is re-derived from the true cell height
    /// (measured at 21.0, not 21.6 or 21.9). What IS shared is the arithmetic below, which
    /// is where the two panes were actually diverging.
    /// </remarks>
    internal const double CellPx = 11.43;

    internal const double LinePx = 21.9;

    /// <summary>
    /// The pane's usable height in design px for a terminal <paramref name="rows"/> tall.
    /// The terminal reports whole LINES, so the pane's true pixel height lies in
    /// [rows, rows+1) lines; half a line is added back so a window that sits just past a
    /// row boundary is not rounded out of it (both panes had discovered this separately —
    /// a 982px-tall 15-inch MacBook lands 4px short of its fourth launcher row).
    /// </summary>
    internal static double PaneHeightPx(int rows) => (rows + 0.5) * LinePx;

    /// <summary>
    /// The pane's width in design px for a terminal <paramref name="cols"/> wide.
    /// </summary>
    internal static double PaneWidthPx(int cols) => cols * CellPx;

    /// <summary>
    /// How many card rows to admit into <paramref name="gridPx"/> of grid height, given
    /// the row height the design PREFERS and the bounds it is willing to live between.
    /// The admitted rows share the grid's height (both stylesheets size card rows
    /// <c>1fr</c>), so this picks the count whose resulting row height lands inside
    /// those bounds — nearest to the preferred height first, then walked up or down
    /// until it does.
    /// </summary>
    /// <remarks>
    /// workspace-wp16o. Both panes used <c>floor(gridPx / stride)</c>, which pins every
    /// row at exactly the stride and discards the remainder as blank space under the
    /// last row: measured median 124px, p90 219px, max 274px over 3663 window sizes on
    /// the link tree, 16–96px on the launcher. Rounding to the nearest count and letting
    /// the rows absorb the remainder leaves nothing over, and sometimes buys a row —
    /// a 600px grid at a 254px stride floors to 2 rows and wastes 92px, but holds 3 rows
    /// of 200px.
    ///
    /// The two walks are what keep that freedom inside the design rather than turning
    /// the panes elastic: never so many rows that a card is squashed below
    /// <paramref name="minRowPx"/>, never so few that one inflates past
    /// <paramref name="maxRowPx"/>. When both cannot hold — only possible at one or two
    /// rows, where the bounds are further apart than the grid — the row count that
    /// over-fills wins and each stylesheet's own <c>max-height</c> caps the card, so the
    /// residue reappears as a band of at most (maxRow − minRow) instead of a whole row.
    /// A stretch with no ceiling was TESTED and rejected: it drove the dead band to zero
    /// everywhere but blew a single row up to 509px on the undocked link tree, and a
    /// 509px card holding two lines of text is worse than the band.
    /// </remarks>
    internal static int RowsForGridPx(double gridPx, double preferredStridePx, double minRowPx, double maxRowPx)
    {
        if (gridPx < minRowPx || preferredStridePx <= 0)
        {
            return 1;
        }

        var rows = Math.Max(1, (int)Math.Round(gridPx / preferredStridePx, MidpointRounding.AwayFromZero));

        while (rows > 1 && gridPx / rows < minRowPx)
        {
            rows--;
        }

        while (gridPx / rows > maxRowPx && gridPx / (rows + 1) >= minRowPx)
        {
            rows++;
        }

        return rows;
    }
}
