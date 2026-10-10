// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the hierarchical link tree view including headers, nodes, and group headers.
/// </summary>
internal class LinkTreeRenderer
{
    /// <summary>
    /// The shortest and tallest a story row may be drawn, in design px: the design's own
    /// <c>clamp(165px, 16.2vw, 242px)</c> card bounds plus the 12px separator.
    /// </summary>
    /// <remarks>
    /// workspace-wp16o. <see cref="ShellPaneCardStridePx"/> is the row height the design
    /// PREFERS at a given pane width; these are the bounds it is willing to live between
    /// at any width. A fixed stride can never divide an arbitrary grid height, so the
    /// remainder was thrown away as blank space at the bottom of the pane — measured
    /// median 124px, p90 219px, max 274px over 3663 window sizes, and up to 255px
    /// (a whole row's worth) on the undocked pane. Letting the admitted rows share the
    /// grid's real height, inside these bounds, spends the remainder instead of
    /// discarding it — and sometimes buys a row: a 600px grid at the design's 254px
    /// stride floors to 2 rows and wastes 92px, but fits 3 rows of 200px.
    /// </remarks>
    internal const double ShellPaneMinRowPx = 165 + 12;
    internal const double ShellPaneMaxRowPx = 242 + 12;

    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";

    // The pane's cell/line px come from the shared model (workspace-vt6o7); the ratios
    // are a property of the terminal font at the pane's 18px scale, not of either pane.
    // Pinned end-to-end by gate-linktree-html.mjs check A12.
    private const double PaneCellPx = PanePxModel.CellPx;
    private const double PaneLinePx = PanePxModel.LinePx;

    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public LinkTreeRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    public void RenderHeader(PageMetadata metadata, string url, RenderOptions options, int linkCount = 0, int sectionCount = 0)
    {
        var width = Math.Max(1, options.TerminalWidth - 2);
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        var title = RenderHelpers.TruncateText(metadata.Title ?? "Untitled", Math.Max(1, width / 2));
        var subtitle = RenderHelpers.TruncateText(
            BuildHeaderSubtitle(metadata, url, linkCount, sectionCount),
            Math.Max(1, width - 6));

        var borderColor = p.GetDimFg().AnsiFg;
        var titleWidth = RenderHelpers.GetDisplayWidth(title);
        var subtitleWidth = RenderHelpers.GetDisplayWidth(subtitle);
        var boxWidth = Math.Max(titleWidth + 6, Math.Min(width - 2, 78));

        // ╭─ Title ──────────────────────────────────────╮
        // visible cells = " " + "╭─ " + title + " " + dashes + "╮" = boxWidth + 3
        var titlePad = Math.Max(0, boxWidth - titleWidth - 3);

        // Page title wears the design's title pink (luminance ladder: titles and
        // headlines are bright pink bold, workspace-7t0a.3).
        _helpers.WriteLine(
            $" {borderColor}\u256d\u2500 {Reset}{p.HeaderTitleFg.AnsiFg}{Bold}{title}{Reset}" +
            $" {borderColor}{new string('\u2500', titlePad)}\u256e{Reset}");

        // │ subtitle                                      │
        // visible cells = " " + "│ " + subtitle + spaces + "│" = boxWidth + 3
        var subtitlePad = Math.Max(0, boxWidth - subtitleWidth - 1);
        _helpers.WriteLine(
            $" {borderColor}\u2502 {Reset}{p.SecondaryText.AnsiFg}{subtitle}{new string(' ', subtitlePad)}{Reset}" +
            $"{borderColor}\u2502{Reset}");

        // ╰──────────────────────────────────────────────╯
        _helpers.WriteLine(
            $" {borderColor}\u2570{new string('\u2500', boxWidth)}\u256f{Reset}");
    }

    public void RenderLinkTree(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        tree.EnsureSelection();
        RenderLinkList(tree, context, maxLines, options);
    }

    public void RenderLinkNode(LinkNode node, bool isSelected, RenderOptions options, string? searchQuery = null)
    {
        if (node.IsGroupHeader)
        {
            RenderGroupHeader(node, isSelected, options);
            return;
        }

        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var indent = new string(' ', (node.Depth * 2) + 4);
        var prefix = isSelected ? "\u2192" : " ";

        string colorStart;
        if (isSelected)
        {
            colorStart = $"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg}";
        }
        else
        {
            colorStart = node.Link.Type switch
            {
                LinkType.Content => p.HeaderTitleFg.AnsiFg,
                LinkType.Navigation => p.LinkNavigation.AnsiFg,
                LinkType.External => p.LinkExternal.AnsiFg,
                LinkType.Footer => p.LinkFooter.AnsiFg,
                _ => p.PrimaryText.AnsiFg
            };
        }

        string collapseIndicator;
        if (node.Children.Count > 0)
        {
            collapseIndicator = node.CollapseState == NodeCollapseState.Expanded ? "\u25bc" : "\u25b6";
        }
        else
        {
            collapseIndicator = " ";
        }

        var displayText = RenderHelpers.TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);

        // workspace-6z3a.5: mark WHY this row matched the active search. Only
        // the displayText segment is highlighted — the line is composed inline
        // with prefix/indent/colors, so whole-line highlighting doesn't fit.
        displayText = HighlightSegment(displayText, searchQuery, p, colorStart);
        _helpers.WriteLine($"{colorStart}{indent}{prefix}{collapseIndicator} {displayText}{Reset}");
    }

    /// <summary>
    /// Renders a card-style group header, dispatching to sub-section or top-level rendering.
    /// </summary>
    public void RenderGroupHeader(LinkNode node, bool isSelected, RenderOptions options, string sectionIndicator = "")
    {
        if (node.Link.HeaderType == HeaderType.SubSection)
        {
            RenderSubSectionHeader(node, isSelected, options, sectionIndicator);
            return;
        }

        RenderTopLevelGroupHeader(node, isSelected, options, sectionIndicator);
    }

    internal static int GetCardHeight(int availableLines)
    {
        if (availableLines < 10)
        {
            return 1;
        }

        if (availableLines < 25)
        {
            return 2;
        }

        return 3;
    }

    /// <summary>
    /// The HTML link-tree pane's column count: ALWAYS 2, at every window width and
    /// docked or not, matching the terminal grid's fixed 2-column contract
    /// (<see cref="ResponsiveGrid.FixedColumns"/>, workspace-21uy).
    /// </summary>
    /// <remarks>
    /// workspace-h2caq made this a responsive mirror of "Link Tree.dc.html"'s
    /// <c>repeat(auto-fill, minmax(min(clamp(420px,40vw,700px),100%),1fr))</c>. That
    /// floored the minimum track at 420px, so the docked-sidecar split (~790px of
    /// pane, measured) collapsed to ONE column — the exact regression
    /// <see cref="ResponsiveGrid"/>'s class remarks already record as rejected, and
    /// the user rejected it again on 2026-07-26. The design's minimum track LOSES to
    /// the 2-column contract: a docked card is ~365px and the headline/byline type
    /// scales down with it through the stylesheet's own clamp()s. The pane's CSS
    /// lays out exactly <c>var(--cols)</c> tracks from this number
    /// (linktree.css <c>#grid</c>), so nothing downstream re-derives a count.
    /// Terminal-mode rendering never calls this.
    /// </remarks>
    internal static int ShellPaneColumns(int terminalWidth) => ResponsiveGrid.FixedColumns;

    /// <summary>
    /// One story row's vertical footprint in the HTML pane, in design px: the
    /// design's fixed <c>clamp(165px,16.2vw,242px)</c> card plus the 12px ╶──╴
    /// separator beneath it (linktree.css <c>.card</c> / <c>.card-sep</c>).
    /// </summary>
    internal static double ShellPaneCardStridePx(double paneWidthPx)
        => Math.Clamp(0.162 * paneWidthPx, 165, 242) + 12;

    /// <summary>
    /// Everything the HTML pane spends above and below the story grid, in design px:
    /// column padding, the masthead (eyebrow, CRT page title, meta line), the top
    /// rule, and the footer block (▼-more mark, rule, key hints).
    /// </summary>
    /// <remarks>
    /// MEASURED, not derived from the stylesheet. <c>#grid</c> is the only shrinkable
    /// child of <c>#column</c> (linktree.css <c>flex: 1 1 auto</c>), so the grid's own
    /// height IS the budget and chrome is exactly
    /// <c>#column.height − #grid.height</c> off the live pane. Measured with
    /// gates/probe-linktree-budget.mjs on 2026-07-26, docked and undocked:
    /// 233px at 789px of pane, 236 at 877, 247 at 974, 258 at 1070, 300 at 1330,
    /// 311 at 1482 and at 1650 (the clamp()s flatten past ~1500px). A clamped line
    /// through those points reproduces every one to within ~13px — a fourteenth of a
    /// card stride. The previous version summed linktree.css's clamp()s instead; it
    /// happened to land close here, but the same approach measured ~55px light on the
    /// launcher pane, so it is not a method to trust. Re-run the probe if the pane's
    /// chrome changes.
    ///
    /// Re-measured 2026-07-26 with the shared gates/probe-pane-px.mjs across both panes,
    /// twelve window sizes, docked and undocked: mean error 5.3px, max 27px. The 27px is
    /// the ONE outlier and it is at the narrow end — a 638px pane, where the clamp floor
    /// of 233 under-reads a real 260. It costs nothing at that size (the row count still
    /// matched what the pane drew, and the residual band was 2px) because the rows share
    /// the grid and absorb it, but the floor is the least-measured part of this model and
    /// the first thing to re-fit if a narrow pane ever clips.
    /// </remarks>
    internal static double ShellPaneChromePx(double w)
        => Math.Clamp(144 + (0.108 * w), 233, 313);

    /// <summary>
    /// Lines per story row in shell mode: the design's card stride expressed in
    /// terminal lines, so one admitted row costs what the pane actually draws.
    /// </summary>
    internal static int ShellPaneCellHeight(int terminalWidth)
        => Math.Max(1, (int)Math.Round(ShellPaneCardStridePx(terminalWidth * PaneCellPx) / PaneLinePx));

    /// <summary>
    /// How many story ROWS the pane can actually draw: its grid height divided by the
    /// card stride, both in px. Counting in px is the point — quantising the stride
    /// into whole 21px terminal lines throws away up to 20px a row, and the loss
    /// compounds down a tall pane (workspace-55jdj).
    /// </summary>
    /// <remarks>
    /// The terminal reports whole LINES, so the pane's true pixel height lies in
    /// [rows, rows+1) lines; half a line is added back so a window that sits just
    /// past a row boundary is not rounded out of it. Validated against ten live
    /// measurements (gates/probe-linktree-budget.mjs, docked and undocked, 800–1300px
    /// tall): this reproduces the row count the pane actually renders in every one.
    /// </remarks>
    internal static int ShellPaneRows(int terminalWidth, int terminalHeight)
    {
        var w = terminalWidth * PaneCellPx;
        var gridPx = ((terminalHeight + 0.5) * PaneLinePx) - ShellPaneChromePx(w);
        return RowsForGridPx(gridPx, ShellPaneCardStridePx(w));
    }

    /// <summary>
    /// How many story rows to admit into <paramref name="gridPx"/> of grid: the shared
    /// pane model, bounded by the design's own card clamp (workspace-wp16o).
    /// </summary>
    internal static int RowsForGridPx(double gridPx, double preferredStridePx)
        => PanePxModel.RowsForGridPx(gridPx, preferredStridePx, ShellPaneMinRowPx, ShellPaneMaxRowPx);

    /// <summary>
    /// The line budget the admission loop honours
    /// (<see cref="LinkTreeLayout.PaneBudgetLines"/>): the pane's grid height priced
    /// in lines at the CARD's own px-per-line, not the terminal's.
    /// </summary>
    /// <remarks>
    /// The conversion rate matters. A section header costs the loop ~3 lines but takes
    /// only ~20px of pane, while a card row costs <see cref="ShellPaneCellHeight"/>
    /// lines and takes the full stride. Converting the budget at the terminal's 21.9px
    /// a line therefore lets a single section header eat ~45px of budget it never
    /// spends on screen — enough to push the last card row out and leave the dead band
    /// the user reported. Pricing the budget at <c>stride / cellHeight</c> instead
    /// keeps card rows exact and leaves headers the slack they actually need.
    /// </remarks>
    internal static int ShellPaneGridLines(int terminalWidth, int terminalHeight)
    {
        var w = terminalWidth * PaneCellPx;
        var gridPx = ((terminalHeight + 0.5) * PaneLinePx) - ShellPaneChromePx(w);
        var cellHeight = ShellPaneCellHeight(terminalWidth);
        var rows = RowsForGridPx(gridPx, ShellPaneCardStridePx(w));

        // The budget has to do two things at once: never let a card row past
        // ShellPaneRows, and never let a SECTION HEADER cost one. Headers are priced in
        // whole terminal lines (~3) but take only ~46px of pane — a fraction of a row —
        // so a budget of exactly rows × cellHeight spends a quarter of a row on every
        // header and drops the last one. Measured: 1650×1000 undocked has grid for 3
        // rows and drew 2, with 85px standing empty, because one header ate 3 of the 36
        // lines (workspace-wp16o).
        //
        // (rows + 1) × cellHeight − 1 is the LARGEST budget that still caps card rows at
        // `rows`: one more row would need (rows + 1) × cellHeight, which is one line more
        // than there is. So headers get every line that cannot buy a row, and not one
        // that can. If they take so much PANE height that the rows fall under the design
        // minimum, linktree.css's min-height makes the grid scroll — the failure
        // direction the project already chose (workspace-z559n).
        return Math.Max(cellHeight, ((rows + 1) * cellHeight) - 1);
    }

    /// <summary>
    /// Computes shared layout parameters from terminal dimensions.
    /// Single source of truth for card dimensions, called by both the renderer and BrowserOrchestrator.
    /// In shell mode (<paramref name="shellPane"/>) the whole geometry follows the
    /// HTML pane — column count from <see cref="ShellPaneColumns"/>, row height and
    /// row budget from <see cref="ShellPaneCellHeight"/> /
    /// <see cref="ShellPaneGridLines"/> — so the pushed window, arrow-key movement,
    /// scrolling, and what the pane can actually DRAW share ONE geometry; terminal
    /// mode keeps the fixed 2-column quarter-screen contract (workspace-ehon).
    /// </summary>
    internal static LinkTreeLayout ComputeLayout(int terminalWidth, int terminalHeight, bool shellPane = false)
    {
        const int headerLines = 3;
        const int statusBarLines = 2;
        const int standardCellHeight = 5;
        const int compactCellHeight = 3;

        var width = Math.Max(1, terminalWidth - 2);
        var availableHeight = Math.Max(4, terminalHeight - headerLines - statusBarLines);

        // Fixed 2-column grid with screen-filling tiles (workspace-21uy):
        // ResponsiveGrid owns both the column contract and the cell-height
        // formula so the launcher and story list keep one card proportion.
        // Short windows keep the compact 3-line card and scroll.
        var columns = shellPane
            ? ShellPaneColumns(terminalWidth)
            : ResponsiveGrid.ColumnsFor(width);

        // In shell mode BOTH axes follow the HTML pane: a row costs what the pane
        // draws for one card, and the budget stops at what is left under the pane's
        // (much taller) chrome. Terminal mode keeps the quarter-screen tile.
        var terminalCellHeight = availableHeight < 15
            ? compactCellHeight
            : ResponsiveGrid.CellHeightFor(terminalHeight, standardCellHeight);
        var cellHeight = shellPane ? ShellPaneCellHeight(terminalWidth) : terminalCellHeight;
        var paneBudgetLines = shellPane
            ? ShellPaneGridLines(terminalWidth, terminalHeight)
            : int.MaxValue;

        // In shell mode PaneBudgetLines is the single binding budget, so VisibleRows
        // must not re-cap it: the admission loop takes min(VisibleRows × CellHeight,
        // PaneBudgetLines), and rounding DOWN here would claw back exactly the header
        // slack the budget just granted. The pane's true card-row capacity is
        // ShellPaneRows; this is that capacity in the units the loop multiplies.
        var visibleRows = shellPane
            ? (paneBudgetLines + cellHeight - 1) / cellHeight
            : Math.Max(1, Math.Min(availableHeight, paneBudgetLines) / cellHeight);
        var cellWidth = ResponsiveGrid.CellWidthFor(width, columns);

        return new LinkTreeLayout(
            Width: width,
            Columns: columns,
            CellHeight: cellHeight,
            CellWidth: cellWidth,
            VisibleRows: visibleRows,
            HeaderLines: headerLines,
            StatusBarLines: statusBarLines,
            PaneBudgetLines: paneBudgetLines);
    }

    /// <summary>
    /// Builds a single line of a link card, dispatching to selected or normal rendering.
    /// Used by the card-based render loop in RenderLinkTree.
    /// </summary>
    internal static string BuildCardLine(
        LinkNode node,
        bool isSelected,
        int cardHeight,
        int lineIndex,
        int width,
        ThemePalette palette,
        IReadOnlySet<string>? cachedUrls = null,
        bool isToggled = false,
        string? searchQuery = null,
        bool drawSeparator = true)
    {
        return isSelected
            ? BuildSelectedCardLine(node, cardHeight, lineIndex, width, palette, isToggled, searchQuery, drawSeparator)
            : BuildNormalCardLine(node, cardHeight, lineIndex, width, palette, isToggled, searchQuery, drawSeparator);
    }

    /// <summary>
    /// workspace-6z3a.5: wraps every case-insensitive occurrence of
    /// <paramref name="query"/> inside <paramref name="text"/> in the theme's
    /// search-highlight colors, then restores <paramref name="resumeAnsi"/>
    /// (the color state the surrounding line was composed with — e.g. the
    /// selected-row background) so the rest of the segment renders unchanged.
    /// Returns the text untouched when the query is absent or empty. The
    /// returned string's VISIBLE length equals the input's, so padding math
    /// based on the plain text stays valid.
    /// </summary>
    internal static string HighlightSegment(string text, string? query, ThemePalette palette, string resumeAnsi)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder();
        var index = 0;
        var found = false;
        while (index < text.Length)
        {
            var pos = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
            {
                sb.Append(text, index, text.Length - index);
                break;
            }

            found = true;
            sb.Append(text, index, pos - index);
            sb.Append(palette.SearchHighlightBg.AnsiBg).Append(palette.SearchHighlightFg.AnsiFg);
            sb.Append(text, pos, query.Length);
            sb.Append(Reset).Append(resumeAnsi);
            index = pos + query.Length;
        }

        return found ? sb.ToString() : text;
    }

    internal static string? FormatDate(DateTime? date)
    {
        if (date == null)
        {
            return null;
        }

        var d = date.Value;
        var now = DateTime.Now;

        if (d.Date == now.Date)
        {
            return "Today";
        }

        if (d.Date == now.Date.AddDays(-1))
        {
            return "Yesterday";
        }

        if (d.Year == now.Year)
        {
            return d.ToString("MMM d");
        }

        return d.ToString("MMM d, yyyy");
    }

    /// <summary>
    /// Gets the effective text width for title wrapping, consistent between selected and normal modes.
    /// Selected cards have accent bar (1 char) + space (1 char) = 2 chars overhead from width.
    /// Normal cards have prefix space/dot (1 char) = 1 char overhead from width.
    /// Use the narrower (selected) width for both to avoid visual jitter on selection change.
    /// </summary>
    internal static int GetTitleTextWidth(int cellWidth) => Math.Max(1, cellWidth - 2);

    /// <summary>
    /// Number of lines the title may wrap across in a standard card
    /// (workspace-21uy). The cell reserves the top padding line, the
    /// author/date line and the separator, plus one interior padding line on
    /// cells tall enough to have one — everything else belongs to the title so
    /// long headlines fill the tile instead of truncating at two lines.
    /// The classic 5-line card keeps its 2-line title.
    /// </summary>
    internal static int GetTitleLineBudget(int cardHeight)
    {
        return cardHeight >= 5 ? Math.Max(2, cardHeight - 4) : 1;
    }

    /// <summary>
    /// First line of the card's content block (wrapped title + author/date)
    /// when the block is vertically centred in the content area — every cell
    /// line except the trailing separator slot (Card Redesign.dc.html 1a,
    /// workspace-e86u). The separator slot stays reserved even on the bottom
    /// grid row (where the rule is suppressed) so all rows share one geometry.
    /// At the 5-line floor with a 1-line title this is line 1 — the classic
    /// card layout.
    /// </summary>
    internal static int GetContentTopLine(int cardHeight, int contentLineCount)
    {
        return Math.Max(0, (cardHeight - 1 - contentLineCount) / 2);
    }

    /// <summary>
    /// Wraps the title into at most <paramref name="maxLines"/> display lines.
    /// When the full text needs more lines than that, the final line carries
    /// the ellipsized remainder.
    /// </summary>
    internal static IReadOnlyList<string> GetWrappedTitleLines(string displayText, int textWidth, int maxLines)
    {
        if (textWidth <= 0 || string.IsNullOrEmpty(displayText))
        {
            return new[] { RenderHelpers.TruncateText(displayText ?? string.Empty, Math.Max(1, textWidth)) };
        }

        var wrapped = RenderHelpers.WrapText(displayText, textWidth);
        if (wrapped.Count <= maxLines)
        {
            return wrapped;
        }

        var clamped = wrapped.Take(Math.Max(0, maxLines - 1)).ToList();
        clamped.Add(RenderHelpers.TruncateText(string.Join(" ", wrapped.Skip(Math.Max(0, maxLines - 1))), textWidth));
        return clamped;
    }

    /// <summary>
    /// Gets a specific wrapped line of the title text (0-indexed) under the
    /// classic 2-line clamp. Returns the requested line, or empty string if the
    /// title doesn't have that many lines; with 3+ wrapped lines the second
    /// line carries the ellipsized remainder.
    /// </summary>
    internal static string GetWrappedTitleLine(string displayText, int textWidth, int lineNumber)
    {
        var lines = GetWrappedTitleLines(displayText, textWidth, 2);
        return lineNumber >= 0 && lineNumber < lines.Count ? lines[lineNumber] : string.Empty;
    }

    /// <summary>
    /// Gets the number of lines a group header occupies at a given card height.
    /// Used by AdjustScrollForSelection to mirror the rendering loop's line accounting.
    /// Sub-section headers are always 2 lines (blank + header) or 1 in compact mode.
    /// Top-level headers are 3 lines when expanded (blank + header + rule) or 2 when collapsed.
    /// </summary>
    internal static int GetLinesForGroupHeader(LinkNode node, int cardHeight)
    {
        if (cardHeight == 1)
        {
            return 1;
        }

        if (node.Link.HeaderType == HeaderType.SubSection)
        {
            return 2;
        }

        return node.CollapseState == NodeCollapseState.Expanded ? 3 : 2;
    }

    /// <summary>
    /// Computes the absolute (row, col) screen position of the title text for the
    /// node at <paramref name="selectedNodeIndex"/> in <paramref name="visibleNodes"/>,
    /// matching the layout produced by <see cref="RenderLinkList"/>.
    /// Returns null when the node is not visible (e.g. scrolled off-screen) or when
    /// <paramref name="visibleNodes"/> is empty/invalid.
    /// </summary>
    /// <param name="visibleNodes">The current visible-node ordering (from NavigationTree.GetVisibleNodes).</param>
    /// <param name="selectedNodeIndex">Index into <paramref name="visibleNodes"/> of the selected node.</param>
    /// <param name="scrollOffset">Current grid-row scroll offset (from NavigationContext.ScrollOffset).</param>
    /// <param name="layout">Active layout, typically obtained from <see cref="ComputeLayout"/>.</param>
    /// <param name="maxLines">Maximum lines available for the link list (viewport height).</param>
    /// <returns>(row, col) of the title text, or null if not currently visible.</returns>
    internal static (int Row, int Col)? TryGetSelectedRowScreenPosition(
        IReadOnlyList<LinkNode> visibleNodes,
        int selectedNodeIndex,
        int scrollOffset,
        LinkTreeLayout layout,
        int maxLines)
    {
        if (visibleNodes == null || visibleNodes.Count == 0)
        {
            return null;
        }

        if (selectedNodeIndex < 0 || selectedNodeIndex >= visibleNodes.Count)
        {
            return null;
        }

        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes.ToList(), layout.Columns);
        var (gridRowIdx, gridCol) = LinkTreeGridMapper.NodeIndexToGridPosition(gridRows, selectedNodeIndex);

        if (gridRowIdx < scrollOffset)
        {
            return null;
        }

        // Account for the leading scroll-up indicator line if present.
        var screenLine = layout.HeaderLines + (scrollOffset > 0 ? 1 : 0);
        var groupCardHeight = layout.CellHeight >= 5 ? 3 : layout.CellHeight;

        for (var r = scrollOffset; r < gridRows.Count; r++)
        {
            var gr = gridRows[r];
            var linesNeeded = gr.IsGroupHeader ? GetLinesForNode(gr.Left, groupCardHeight) : layout.CellHeight;

            if (r == gridRowIdx)
            {
                if (gr.IsGroupHeader)
                {
                    // Group headers don't have a title-text col offset that matches a card —
                    // skip the flash for them.
                    return null;
                }

                // Title line within the card: the centred content block's
                // first line for standard cards (Card Redesign.dc.html 1a —
                // MUST mirror BuildSelectedCardLine or the spotlight lands on
                // the wrong row), row 0 of a compact card.
                var titleLineIdx = 0;
                if (layout.CellHeight >= 5)
                {
                    var isLastColumn = gridCol == layout.Columns - 1;
                    var cellW = isLastColumn
                        ? ResponsiveGrid.LastCellWidthFor(layout.Width, layout.Columns)
                        : layout.CellWidth;
                    var titleLines = GetWrappedTitleLines(
                        gr.Cells[gridCol].Link.DisplayText,
                        GetTitleTextWidth(cellW),
                        GetTitleLineBudget(layout.CellHeight));
                    titleLineIdx = GetContentTopLine(layout.CellHeight, titleLines.Count + 1);
                }

                // Cards write at column 0 of the line. The title text sits after the
                // leading prefix character (accent bar/space/dot) + 1 separator space = col 2.
                const int leftCardTitleCol = 2;

                // Responsive N columns (workspace-ehon): every non-last cell is
                // CellWidth wide with a 1-char divider between, so column c starts
                // at c*(CellWidth+1) and the title text sits leftCardTitleCol into
                // it. The spotlight must land on the SELECTED cell's title, not a
                // fixed left/right pair.
                var col = (gridCol * (layout.CellWidth + 1)) + leftCardTitleCol;

                return (screenLine + titleLineIdx, col);
            }

            // Bail out if this row would have been clipped by the viewport.
            if (screenLine - layout.HeaderLines + linesNeeded > maxLines)
            {
                return null;
            }

            screenLine += linesNeeded;
        }

        return null;
    }

    private static string BuildHeaderSubtitle(PageMetadata metadata, string url, int linkCount, int sectionCount)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(metadata.Author))
        {
            parts.Add(metadata.Author);
        }

        if (metadata.PublishedDate.HasValue)
        {
            parts.Add(FormatDate(metadata.PublishedDate) ?? metadata.PublishedDate.Value.ToString("MMM d, yyyy"));
        }

        parts.Add(LauncherRenderer.ExtractDomain(url));

        if (linkCount > 0)
        {
            parts.Add($"{linkCount} links");
        }

        if (sectionCount > 1)
        {
            parts.Add($"{sectionCount} sections");
        }

        return string.Join(" \u00b7 ", parts);
    }

    private static string BuildSelectedCardLine(
        LinkNode node,
        int cardHeight,
        int lineIndex,
        int width,
        ThemePalette palette,
        bool isToggled = false,
        string? searchQuery = null,
        bool drawSeparator = true)
    {
        var sb = new StringBuilder();
        var selBg = palette.SelectedItemBg.AnsiBg;
        var selFg = palette.SelectedItemFg.AnsiFg;
        var contentWidth = width - 1;

        // Standard cards (workspace-21uy, centred per Card Redesign.dc.html 1a,
        // workspace-e86u): the title wraps across up to GetTitleLineBudget
        // lines, the author/date row sits directly beneath the last title line,
        // and the whole block is vertically centred in the content area.
        // Compact cards keep the classic top-pinned single-line vocabulary.
        IReadOnlyList<string> titleLines;
        int titleLineIdx;
        int authorDateLineIdx;
        int metadataLineIdx;
        if (cardHeight >= 5)
        {
            titleLines = GetWrappedTitleLines(node.Link.DisplayText, GetTitleTextWidth(width), GetTitleLineBudget(cardHeight));
            titleLineIdx = GetContentTopLine(cardHeight, titleLines.Count + 1);
            authorDateLineIdx = titleLineIdx + Math.Max(1, titleLines.Count);
            metadataLineIdx = -1;
        }
        else
        {
            titleLines = Array.Empty<string>();
            titleLineIdx = 0;
            authorDateLineIdx = -1;
            metadataLineIdx = GetMetadataLineIndex(cardHeight);
        }

        var isSeparator = lineIndex == cardHeight - 1 && cardHeight > 1 && drawSeparator;

        // workspace-zlv0 (refines mj9x + 63jj): the selection rectangle fills
        // every row of the cell EXCEPT a drawn separator. The separator is the
        // dim ─ rule that visually divides cell rows from each other — that
        // divider is the cell's bottom border and must survive the highlight
        // (workspace-63jj). On the grid's BOTTOM row the rule is suppressed
        // (Card Redesign.dc.html — the status bar's own rule caps the grid),
        // so the last line falls through to the selBg fill and the highlight
        // reaches the card's bottom edge.
        if (isSeparator)
        {
            return $"{palette.HeaderBorderFg.AnsiFg}{new string('─', width)}{Reset}";
        }

        // workspace-mj9x: the selection rectangle covers the ENTIRE card —
        // top padding and any blank content slots all get selBg. The rail
        // (or check on toggled title) sits inside selBg so column 0 isn't a
        // black gap to the left of the highlight. Rail colour is the design's
        // focus-bar muted green (--tr-focus-bar): HeaderBorderFg equals the
        // selection fill in Phosphor, which made the rail invisible
        // (workspace-e86u; same fix as the launcher's workspace-7t0a.2).
        if (isToggled && lineIndex == titleLineIdx)
        {
            sb.Append($"{selBg}{palette.GetAccentFg().AnsiFg}\u2713");
        }
        else
        {
            sb.Append($"{selBg}{palette.GetMutedFg().AnsiFg}\u258c");
        }

        if (cardHeight >= 5 && lineIndex >= titleLineIdx && lineIndex < titleLineIdx + titleLines.Count)
        {
            var titleLine = titleLines[lineIndex - titleLineIdx];

            // workspace-6z3a.5: highlight the search match; pad from the PLAIN
            // length so the injected ANSI codes don't shift the layout.
            var selResume = $"{selBg}{selFg}{Bold}";
            sb.Append($"{selResume} {HighlightSegment(titleLine, searchQuery, palette, selResume)}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - titleLine.Length))}{Reset}");
        }
        else if (cardHeight < 5 && lineIndex == titleLineIdx)
        {
            var titleLine = RenderHelpers.TruncateText(node.Link.DisplayText, contentWidth - 1);
            var selResume = $"{selBg}{selFg}{Bold}";
            sb.Append($"{selResume} {HighlightSegment(titleLine, searchQuery, palette, selResume)}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - titleLine.Length))}{Reset}");
        }
        else if (lineIndex == authorDateLineIdx || lineIndex == metadataLineIdx)
        {
            // Selected metadata wears the selection foreground (the design's
            // white-on-fill, Card Redesign.dc.html) instead of staying
            // metadata green on the fill.
            var subtitle = GetMetadataSubtitle(node, contentWidth - 1);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                sb.Append($"{selBg}{selFg} {subtitle}");
                sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - subtitle.Length))}{Reset}");
            }
            else
            {
                sb.Append($"{selBg}{new string(' ', contentWidth)}{Reset}");
            }
        }
        else
        {
            // Top padding (line 0 when title sits at line 1) and any other
            // content-free line falls through here with a plain selBg fill so
            // the cell body stays a clean rectangle reaching the top edge —
            // workspace-zlv0. The separator row is the only exception and is
            // handled by the early return at the top.
            sb.Append($"{selBg}{new string(' ', contentWidth)}{Reset}");
        }

        return sb.ToString();
    }

    private static string BuildNormalCardLine(
        LinkNode node,
        int cardHeight,
        int lineIndex,
        int width,
        ThemePalette palette,
        bool isToggled = false,
        string? searchQuery = null,
        bool drawSeparator = true)
    {
        // Standard cards (workspace-21uy, centred per Card Redesign.dc.html 1a,
        // workspace-e86u): title wraps across up to GetTitleLineBudget lines,
        // author/date directly beneath, and the block is vertically centred in
        // the content area. Compact cards keep the classic top-pinned single
        // truncated title line.
        IReadOnlyList<string> titleLines;
        int titleLineIdx;
        int authorDateLineIdx;
        int metadataLineIdx;
        if (cardHeight >= 5)
        {
            titleLines = GetWrappedTitleLines(node.Link.DisplayText, GetTitleTextWidth(width), GetTitleLineBudget(cardHeight));
            titleLineIdx = GetContentTopLine(cardHeight, titleLines.Count + 1);
            authorDateLineIdx = titleLineIdx + Math.Max(1, titleLines.Count);
            metadataLineIdx = -1;
        }
        else
        {
            titleLines = Array.Empty<string>();
            titleLineIdx = 0;
            authorDateLineIdx = -1;
            metadataLineIdx = GetMetadataLineIndex(cardHeight);
        }

        var isTitleLine = cardHeight >= 5
            ? lineIndex >= titleLineIdx && lineIndex < titleLineIdx + titleLines.Count
            : lineIndex == titleLineIdx;
        if (isTitleLine)
        {
            var colorFg = node.Link.Type switch
            {
                LinkType.Content => palette.HeaderTitleFg.AnsiFg,
                LinkType.Navigation => palette.LinkNavigation.AnsiFg,
                LinkType.External => palette.LinkExternal.AnsiFg,
                LinkType.Footer => palette.LinkFooter.AnsiFg,
                _ => palette.PrimaryText.AnsiFg
            };
            var titleLine = cardHeight >= 5
                ? titleLines[lineIndex - titleLineIdx]
                : RenderHelpers.TruncateText(node.Link.DisplayText, width - 1);

            var prefix = isToggled && lineIndex == titleLineIdx ? $"{palette.GetAccentFg().AnsiFg}\u2713{Reset}" : " ";
            var pad = new string(' ', Math.Max(0, width - 1 - titleLine.Length));

            // workspace-6z3a.5: highlight the search match within the title.
            return $"{prefix}{colorFg}{HighlightSegment(titleLine, searchQuery, palette, colorFg)}{pad}{Reset}";
        }

        if (lineIndex == authorDateLineIdx)
        {
            var subtitle = GetMetadataSubtitle(node, width - 1);
            var metaPad = new string(' ', Math.Max(0, width - 1 - subtitle.Length));
            return $" {palette.SecondaryText.AnsiFg}{subtitle}{metaPad}{Reset}";
        }

        if (lineIndex == metadataLineIdx)
        {
            var subtitle = GetMetadataSubtitle(node, width - 1);
            var metaPad = new string(' ', Math.Max(0, width - 1 - subtitle.Length));
            return $" {palette.SecondaryText.AnsiFg}{subtitle}{metaPad}{Reset}";
        }

        // Bottom grid row draws no rule (Card Redesign.dc.html) — the status
        // bar's own rule caps the grid; the line renders as blank padding.
        if (lineIndex == cardHeight - 1 && cardHeight > 1 && drawSeparator)
        {
            return $"{palette.HeaderBorderFg.AnsiFg}{new string('\u2500', width)}{Reset}";
        }

        return new string(' ', width);
    }

    private static string GetMetadataSubtitle(LinkNode node, int maxWidth)
    {
        var dateStr = FormatDate(node.Link.PublishedDate);
        var author = node.Link.Author;

        string subtitle;
        if (!string.IsNullOrEmpty(author) && dateStr != null)
        {
            subtitle = $"{author} \u00b7 {dateStr}";
        }
        else if (!string.IsNullOrEmpty(author))
        {
            subtitle = author;
        }
        else if (dateStr != null)
        {
            subtitle = dateStr;
        }
        else
        {
            subtitle = string.Empty;
        }

        return RenderHelpers.TruncateText(subtitle, maxWidth);
    }

    // Compact cards only — standard (>= 5) cards place the author/date row
    // directly beneath the wrapped title (workspace-21uy).
    private static int GetMetadataLineIndex(int cardHeight)
    {
        return cardHeight >= 3 ? 1 : -1;
    }

    /// <summary>
    /// Counts the STORIES held by grid rows <c>[start, end)</c>, which is what the ▲/▼
    /// pager must report.
    /// </summary>
    /// <remarks>
    /// workspace-u4p2z: the pager reads "N more below", and every neighbouring number —
    /// the header's "6 of 15 shown", the footer's 6/15 — counts stories, so N counting
    /// grid ROWS made the figure read as simply wrong: 15 stories at 2 columns is 8 grid
    /// rows, so "6 of 15 shown" sat beside "5 more below" when 9 were. Group headers own
    /// a full-width row and are not stories, so they never count.
    /// </remarks>
    private static int CountStories(IReadOnlyList<GridRow> rows, int start, int end)
    {
        var stories = 0;
        for (var i = Math.Max(0, start); i < Math.Min(rows.Count, end); i++)
        {
            if (!rows[i].IsGroupHeader)
            {
                stories += rows[i].Cells.Count(n => !n.IsGroupHeader);
            }
        }

        return stories;
    }

    private static int GetLinesForNode(LinkNode node, int cardHeight)
    {
        if (!node.IsGroupHeader)
        {
            return cardHeight;
        }

        return GetLinesForGroupHeader(node, cardHeight);
    }

    /// <summary>
    /// workspace-ae265: whether the page cache holds this page, for the header's "cached" tag.
    /// The cache is keyed by the URL the user REQUESTED, but <see cref="Page.Url"/> is the FINAL
    /// url after any redirect — so a page reached through a redirect never matched on
    /// <c>page.Url</c> alone. Tries both URLs: raw membership first (cheap, exact), then the cache's
    /// own key function (<see cref="UrlNormalizer"/>) so a trailing slash, fragment, or host case
    /// difference between the request URL and the stored key doesn't hide the tag.
    /// </summary>
    private static bool IsPageCached(Page page, IReadOnlySet<string>? cachedUrls)
    {
        if (cachedUrls is null || cachedUrls.Count == 0)
        {
            return false;
        }

        var candidates = string.IsNullOrWhiteSpace(page.RequestedUrl)
            ? new[] { page.Url }
            : new[] { page.RequestedUrl, page.Url };

        if (candidates.Any(cachedUrls.Contains))
        {
            return true;
        }

        var normalizedCached = new HashSet<string>(cachedUrls.Select(UrlNormalizer.Normalize));
        return candidates.Any(c => normalizedCached.Contains(UrlNormalizer.Normalize(c)));
    }

    /// <summary>
    /// Renders the link tree as a 2-column grid of cards.
    /// This is the single LinkTree rendering entry point — there are no other variants.
    /// </summary>
    private void RenderLinkList(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, options.ShellPane);
        var visibleNodes = tree.GetVisibleNodes().ToList();
        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var startRow = context.ScrollOffset;

        var linesUsed = 0;
        var rowsRendered = 0;

        // In shell mode the HTML pane's drawable height is the tighter constraint —
        // admit the SAME window here so the terminal grid, the pushed view-model and
        // the ▲/▼ counts never disagree (workspace-h2caq).
        var budget = Math.Min(maxLines, layout.PaneBudgetLines);

        // Scroll-up indicator. The count is STORIES, not grid rows (workspace-u4p2z).
        if (startRow > 0)
        {
            RenderScrollIndicator(layout.Width, p, true, CountStories(gridRows, 0, startRow));
            linesUsed++;
        }

        for (var row = startRow; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];
            var groupCardHeight = layout.CellHeight >= 5 ? 3 : layout.CellHeight;
            var linesNeeded = gr.IsGroupHeader ? GetLinesForNode(gr.Left, groupCardHeight) : layout.CellHeight;

            // Reserve 1 line for scroll-down indicator if more rows follow — in the
            // TERMINAL, where that indicator is a line of the grid's own budget. In
            // shell mode it is not: the pane draws "▼ N more below" in its footer,
            // which lives OUTSIDE #grid and is already inside ShellPaneChromePx. So
            // charging it there docked a line off a budget priced in card rows, and
            // whenever the budget came out an exact multiple of the cell height that
            // one line cost a WHOLE card row — 10.6% of window sizes, every one of
            // terminal height 35/43/51/59 (workspace-wm1kd).
            var hasMoreAfter = row + 1 < gridRows.Count;
            var available = hasMoreAfter && !options.ShellPane ? budget - 1 : budget;

            if (linesUsed + linesNeeded > available)
            {
                break;
            }

            // Is this the BOTTOM rendered row? Mirror the loop's own admission
            // test one row ahead: if the next row won't fit (or none exists),
            // this row's cards draw no bottom rule (Card Redesign.dc.html,
            // workspace-e86u) — the status bar's rule caps the grid instead.
            var isBottomRow = true;
            if (row + 1 < gridRows.Count)
            {
                var next = gridRows[row + 1];
                var nextNeeded = next.IsGroupHeader ? GetLinesForNode(next.Left, groupCardHeight) : layout.CellHeight;
                var nextAvailable = row + 2 < gridRows.Count ? budget - 1 : budget;
                isBottomRow = linesUsed + linesNeeded + nextNeeded > nextAvailable;
            }

            if (gr.IsGroupHeader)
            {
                var selIndicator = string.Empty;
                if (tree.IsSectionFullySelected(gr.Left))
                {
                    selIndicator = "\u2713 ";
                }
                else if (tree.IsSectionPartiallySelected(gr.Left))
                {
                    selIndicator = "\u25d0 ";
                }

                RenderGroupHeader(gr.Left, gr.Left.IsSelected, options, selIndicator);
            }
            else
            {
                RenderGridRow(gr, layout, p, options.CachedUrls, tree.SelectedNodeIds, context.SearchQuery, drawSeparator: !isBottomRow);
            }

            linesUsed += linesNeeded;
            rowsRendered++;
        }

        // Scroll-down indicator. Rows decide WHETHER there is more; the number shown is
        // the STORIES in them (workspace-u4p2z).
        if (startRow + rowsRendered < gridRows.Count)
        {
            RenderScrollIndicator(
                layout.Width, p, false, CountStories(gridRows, startRow + rowsRendered, gridRows.Count));
        }
    }

    /// <summary>
    /// Projects the link tree's current window into the shell-channel view-model
    /// ("Link Tree.dc.html", workspace-vingi). MUST mirror
    /// <see cref="RenderLinkList"/>'s admission loop exactly — same grid rows,
    /// same scroll offset, same line budget — so the HTML pane shows precisely
    /// the rows the TUI admitted and the selection can never sit outside the
    /// pane's window.
    /// </summary>
#pragma warning disable SA1202 // kept beside the render loop it must mirror
    internal static LinkTreeViewState BuildViewState(
        Page page,
        NavigationContext context,
        RenderOptions options,
        int maxLines,
        int linkCount,
        int sectionCount)
    {
        var tree = page.LinkTree!;
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, options.ShellPane);
        var visibleNodes = tree.GetVisibleNodes().ToList();
        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var startRow = Math.Clamp(context.ScrollOffset, 0, Math.Max(0, gridRows.Count - 1));

        var linesUsed = startRow > 0 ? 1 : 0; // the ▲ scroll-up indicator line
        var groupCardHeight = layout.CellHeight >= 5 ? 3 : layout.CellHeight;
        var rowsRendered = 0;
        var shownStories = 0;
        var selectedIndex = -1;
        var items = new List<LinkTreeItem>();

        // Never admit more than the HTML pane can draw (workspace-h2caq).
        var budget = Math.Min(maxLines, layout.PaneBudgetLines);

        for (var row = startRow; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];
            var linesNeeded = gr.IsGroupHeader ? GetLinesForNode(gr.Left, groupCardHeight) : layout.CellHeight;

            // Mirrors RenderLinkList's reserve exactly, including the shell-mode
            // exemption (workspace-wm1kd) — the two loops must admit the same window or
            // the pane and the terminal grid disagree about what the user selected.
            var hasMoreAfter = row + 1 < gridRows.Count;
            var available = hasMoreAfter && !options.ShellPane ? budget - 1 : budget;
            if (linesUsed + linesNeeded > available)
            {
                break;
            }

            foreach (var node in gr.Cells)
            {
                // node.IsSelected is what the terminal grid paints (RenderGridRow /
                // RenderGroupHeader) — the pane must use the SAME flag, never a
                // parallel notion of selection.
                if (node.IsSelected)
                {
                    selectedIndex = items.Count;
                }

                if (node.IsGroupHeader)
                {
                    string? selectState = null;
                    if (tree.IsSectionFullySelected(node))
                    {
                        selectState = "full";
                    }
                    else if (tree.IsSectionPartiallySelected(node))
                    {
                        selectState = "partial";
                    }

                    items.Add(new LinkTreeItem
                    {
                        Kind = "section",
                        Title = node.Link.DisplayText,
                        Count = node.Children.Count,
                        Collapsed = node.CollapseState != NodeCollapseState.Expanded,
                        SelectState = selectState,
                    });
                }
                else
                {
                    var meta = GetMetadataSubtitle(node, 400);
                    items.Add(new LinkTreeItem
                    {
                        Kind = "story",
                        Title = node.Link.DisplayText,
                        Url = node.Link.Url,
                        Meta = string.IsNullOrEmpty(meta) ? null : meta,
                        LinkType = node.Link.Type.ToString().ToLowerInvariant(),
                        Toggled = tree.IsNodeSelected(node),
                    });
                    shownStories++;
                }
            }

            linesUsed += linesNeeded;
            rowsRendered++;
        }

        string? domain = null;
        try
        {
            domain = new Uri(page.Url).Host;
        }
        catch (UriFormatException)
        {
            // Non-absolute URL — the header simply omits the domain.
        }

        var progress = options.CacheProgress;
        return new LinkTreeViewState
        {
            Title = page.Metadata.Title,
            Domain = domain,
            LinkCount = linkCount,
            SectionCount = sectionCount,
            Cached = IsPageCached(page, options.CachedUrls),
            Columns = layout.Columns,
            Items = items,
            SelectedIndex = selectedIndex,

            // The pane renders these as "▲ N more above" / "▼ N more below", so N is
            // STORIES — the unit every other number in that chrome uses (workspace-u4p2z).
            MoreAbove = CountStories(gridRows, 0, startRow),
            MoreBelow = CountStories(gridRows, startRow + rowsRendered, gridRows.Count),
            ShownStories = shownStories,
            TotalStories = visibleNodes.Count(n => !n.IsGroupHeader),
            SelectedCount = tree.SelectionCount,
            Cache = progress is { TotalCacheableLinks: > 0 }
                ? new LinkTreeCacheMeter { Done = progress.CachedCount, Total = progress.TotalCacheableLinks }
                : null,
            Docked = options.BrowserDocked,
        };
    }
#pragma warning restore SA1202

    /// <summary>
    /// Renders a sub-section header with visually lighter style than top-level groups.
    /// Standard mode (cardHeight >= 2): blank line + thin-rule title line.
    /// Compact mode (cardHeight == 1): single thin-rule title line.
    /// Format: ' ─ Title (count) ────────'
    /// </summary>
    private void RenderSubSectionHeader(LinkNode node, bool isSelected, RenderOptions options, string sectionIndicator = "")
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Max(1, options.TerminalWidth - 2);
        var availableLines = Math.Max(3, options.TerminalHeight - 9);
        var cardHeight = GetCardHeight(availableLines);
        var isExpanded = node.CollapseState == NodeCollapseState.Expanded;
        var collapseIndicator = isExpanded ? "\u25bc" : "\u25b6";
        var childCount = node.Children.Count;
        var showCount = !isExpanded || childCount >= 5;
        var countSuffix = showCount ? $" ({childCount})" : string.Empty;
        var titleText = $"{sectionIndicator}{collapseIndicator} {node.Link.DisplayText}{countSuffix}";
        var headerLabel = $" \u2500 {titleText} ";

        // Fill remaining width with thin rule (no box corners — avoids broken box appearance)
        var ruleLen = Math.Max(0, width - headerLabel.Length - 1);
        var headerLine = $"{headerLabel}{new string('\u2500', ruleLen)}";
        var truncLine = RenderHelpers.TruncateText(headerLine, width - 1);

        if (cardHeight >= 2)
        {
            // Line 0: blank line
            if (isSelected)
            {
                _helpers.WriteLine(
                    $"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}" +
                    $"{p.SelectedItemBg.AnsiBg}{new string(' ', Math.Max(0, width - 1))}{Reset}");
            }
            else
            {
                _helpers.WriteLine();
            }
        }

        // Header line
        if (isSelected)
        {
            var sb = new StringBuilder();
            sb.Append($"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}");
            sb.Append($"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg}{truncLine}");
            sb.Append($"{new string(' ', Math.Max(0, width - 1 - truncLine.Length))}{Reset}");
            _helpers.WriteLine(sb.ToString());
        }
        else
        {
            _helpers.WriteLine($" {p.SecondaryText.AnsiFg}{truncLine}{Reset}");
        }
    }

    /// <summary>
    /// Renders a top-level group header (Navigation, External, Footer).
    /// Standard mode (cardHeight >= 2): blank line + header text [+ thin rule for expanded].
    /// Compact mode (cardHeight == 1): single line.
    /// Selected headers get accent bar and highlight background.
    /// </summary>
    private void RenderTopLevelGroupHeader(LinkNode node, bool isSelected, RenderOptions options, string sectionIndicator = "")
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Max(1, options.TerminalWidth - 2);
        var availableLines = Math.Max(3, options.TerminalHeight - 9);
        var cardHeight = GetCardHeight(availableLines);
        var isExpanded = node.CollapseState == NodeCollapseState.Expanded;
        var collapseIndicator = isExpanded ? "\u25bc" : "\u25b6";
        var childCount = node.Children.Count;
        var headerText = $"{sectionIndicator}{collapseIndicator} {node.Link.DisplayText.ToUpperInvariant()} ({childCount})";

        if (cardHeight == 1)
        {
            // Compact: 1 line for both expanded and collapsed
            var truncText = RenderHelpers.TruncateText(headerText, width - 2);
            if (isSelected)
            {
                var sb = new StringBuilder();
                sb.Append($"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}");
                sb.Append($"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg} {truncText}");
                sb.Append($"{new string(' ', Math.Max(0, width - 2 - truncText.Length))}{Reset}");
                _helpers.WriteLine(sb.ToString());
            }
            else
            {
                var color = isExpanded ? $"{p.PrimaryText.AnsiFg}{Bold}" : p.SecondaryText.AnsiFg;
                _helpers.WriteLine($" {color}{truncText}{Reset}");
            }

            return;
        }

        // Standard/Expanded card mode: blank line + header text [+ thin rule for expanded]
        var truncHeader = RenderHelpers.TruncateText(headerText, width - 2);

        // Line 0: blank line
        if (isSelected)
        {
            _helpers.WriteLine(
                $"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}" +
                $"{p.SelectedItemBg.AnsiBg}{new string(' ', Math.Max(0, width - 1))}{Reset}");
        }
        else
        {
            _helpers.WriteLine();
        }

        // Line 1: header text with collapse indicator and child count
        if (isSelected)
        {
            var sb = new StringBuilder();
            sb.Append($"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}");
            sb.Append($"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg} {truncHeader}");
            sb.Append($"{new string(' ', Math.Max(0, width - 2 - truncHeader.Length))}{Reset}");
            _helpers.WriteLine(sb.ToString());
        }
        else if (isExpanded)
        {
            _helpers.WriteLine($" {p.PrimaryText.AnsiFg}{Bold}{truncHeader}{Reset}");
        }
        else
        {
            _helpers.WriteLine($" {p.SecondaryText.AnsiFg}{truncHeader}{Reset}");
        }

        // Line 2: blank line below header (gives breathing room before card grid)
        if (isExpanded)
        {
            if (isSelected)
            {
                _helpers.WriteLine(
                    $"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}" +
                    $"{p.SelectedItemBg.AnsiBg}{new string(' ', Math.Max(0, width - 1))}{Reset}");
            }
            else
            {
                _helpers.WriteLine();
            }
        }
    }

    private void RenderGridRow(GridRow row, LinkTreeLayout layout, ThemePalette p, IReadOnlySet<string>? cachedUrls = null, HashSet<Guid>? selectedIds = null, string? searchQuery = null, bool drawSeparator = true)
    {
        for (var lineIdx = 0; lineIdx < layout.CellHeight; lineIdx++)
        {
            var sb = new StringBuilder();
            var isSeparatorLine = lineIdx == layout.CellHeight - 1 && layout.CellHeight > 1 && drawSeparator;

            // Responsive N columns (workspace-ehon): render one cell per column
            // slot, a divider between them (\u253c on the separator line so the card
            // rule reads as continuous, \u2502 otherwise), with the last column
            // absorbing the width remainder. Slots past this row's cell count are
            // blank-padded so a short last row still fills the full width.
            for (var col = 0; col < layout.Columns; col++)
            {
                if (col > 0)
                {
                    // Structural chrome (--tr-border #005f00, workspace-7t0a.3) \u2014
                    // matches the launcher grid so the skeleton recedes.
                    var divider = isSeparatorLine ? "\u253c" : "\u2502";
                    sb.Append($"{p.HeaderBorderFg.AnsiFg}{divider}{Reset}");
                }

                var isLastCol = col == layout.Columns - 1;
                var cellW = isLastCol
                    ? ResponsiveGrid.LastCellWidthFor(layout.Width, layout.Columns)
                    : layout.CellWidth;

                if (col < row.Cells.Count)
                {
                    var node = row.Cells[col];
                    var toggled = selectedIds != null && selectedIds.Contains(node.Id);
                    sb.Append(BuildCardLine(node, node.IsSelected, layout.CellHeight, lineIdx, cellW, p, cachedUrls, toggled, searchQuery, drawSeparator));
                }
                else
                {
                    sb.Append(new string(' ', cellW));
                }
            }

            _helpers.WriteLine(sb.ToString());
        }
    }

    private void RenderScrollIndicator(int width, ThemePalette p, bool isUp, int count)
    {
        var arrow = isUp ? "\u25b2" : "\u25bc";
        var direction = isUp ? "above" : "below";
        var indicator = $"{arrow} {count} more {direction}";
        var indicatorPad = Math.Max(0, (width - indicator.Length) / 2);
        _helpers.WriteLine(
            $"{new string(' ', indicatorPad)}{p.SecondaryText.AnsiFg}{Dim}{indicator}{Reset}" +
            $"{new string(' ', Math.Max(0, width - indicatorPad - indicator.Length))}");
    }
}
