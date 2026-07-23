// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the hierarchical link tree view including headers, nodes, and group headers.
/// </summary>
internal class LinkTreeRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
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
    /// Computes shared layout parameters from terminal dimensions.
    /// Single source of truth for card dimensions, called by both the renderer and BrowserOrchestrator.
    /// </summary>
    internal static LinkTreeLayout ComputeLayout(int terminalWidth, int terminalHeight)
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
        var columns = ResponsiveGrid.ColumnsFor(width);
        var cellHeight = availableHeight < 15
            ? compactCellHeight
            : ResponsiveGrid.CellHeightFor(terminalHeight, standardCellHeight);

        var visibleRows = Math.Max(1, availableHeight / cellHeight);
        var cellWidth = ResponsiveGrid.CellWidthFor(width, columns);

        return new LinkTreeLayout(
            Width: width,
            Columns: columns,
            CellHeight: cellHeight,
            CellWidth: cellWidth,
            VisibleRows: visibleRows,
            HeaderLines: headerLines,
            StatusBarLines: statusBarLines);
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
        string? searchQuery = null)
    {
        return isSelected
            ? BuildSelectedCardLine(node, cardHeight, lineIndex, width, palette, isToggled, searchQuery)
            : BuildNormalCardLine(node, cardHeight, lineIndex, width, palette, isToggled, searchQuery);
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

                // Title line within the card: row 1 of a 5-row card, row 0 of a compact card.
                var titleLineIdx = layout.CellHeight >= 5 ? 1 : 0;

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
        string? searchQuery = null)
    {
        var sb = new StringBuilder();
        var accentFg = palette.HeaderBorderFg.AnsiFg;
        var selBg = palette.SelectedItemBg.AnsiBg;
        var selFg = palette.SelectedItemFg.AnsiFg;
        var contentWidth = width - 1;

        var titleLineIdx = cardHeight >= 5 ? 1 : 0;
        var isSeparator = lineIndex == cardHeight - 1 && cardHeight > 1;

        // Standard cards (workspace-21uy): the title wraps across up to
        // GetTitleLineBudget lines starting at line 1, the author/date row sits
        // directly beneath the last title line, and any slack pads before the
        // separator. Compact cards keep the classic single-line vocabulary.
        IReadOnlyList<string> titleLines;
        int authorDateLineIdx;
        int metadataLineIdx;
        if (cardHeight >= 5)
        {
            titleLines = GetWrappedTitleLines(node.Link.DisplayText, GetTitleTextWidth(width), GetTitleLineBudget(cardHeight));
            authorDateLineIdx = titleLineIdx + Math.Max(1, titleLines.Count);
            metadataLineIdx = -1;
        }
        else
        {
            titleLines = Array.Empty<string>();
            authorDateLineIdx = -1;
            metadataLineIdx = GetMetadataLineIndex(cardHeight);
        }

        // workspace-zlv0 (refines mj9x + 63jj): the selection rectangle fills
        // every row of the cell EXCEPT the separator. The separator is the
        // dim ─ rule that visually divides cell rows from each other — that
        // divider is the cell's bottom border and must survive the highlight
        // (workspace-63jj). Top padding, title rows, blank content slots, all
        // get selBg so the green box still reaches the top edge.
        if (isSeparator)
        {
            return $"{palette.HeaderBorderFg.AnsiFg}{new string('─', width)}{Reset}";
        }

        // workspace-mj9x: the selection rectangle covers the ENTIRE card —
        // top padding, separator row, and any blank content slots all get
        // selBg. The accent bar (or check on toggled title) sits inside selBg
        // so column 0 isn't a black gap to the left of the highlight.
        if (isToggled && lineIndex == titleLineIdx)
        {
            sb.Append($"{selBg}{palette.GetAccentFg().AnsiFg}\u2713");
        }
        else
        {
            sb.Append($"{selBg}{accentFg}\u258c");
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
            var subtitle = GetMetadataSubtitle(node, contentWidth - 1);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                sb.Append($"{selBg}{palette.SecondaryText.AnsiFg} {subtitle}");
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
        string? searchQuery = null)
    {
        var titleLineIdx = cardHeight >= 5 ? 1 : 0;

        // Standard cards (workspace-21uy): title wraps across up to
        // GetTitleLineBudget lines from line 1, author/date directly beneath,
        // slack pads before the separator. Compact cards keep the classic
        // single truncated title line.
        IReadOnlyList<string> titleLines;
        int authorDateLineIdx;
        int metadataLineIdx;
        if (cardHeight >= 5)
        {
            titleLines = GetWrappedTitleLines(node.Link.DisplayText, GetTitleTextWidth(width), GetTitleLineBudget(cardHeight));
            authorDateLineIdx = titleLineIdx + Math.Max(1, titleLines.Count);
            metadataLineIdx = -1;
        }
        else
        {
            titleLines = Array.Empty<string>();
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

        if (lineIndex == cardHeight - 1 && cardHeight > 1)
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

    private static int GetLinesForNode(LinkNode node, int cardHeight)
    {
        if (!node.IsGroupHeader)
        {
            return cardHeight;
        }

        return GetLinesForGroupHeader(node, cardHeight);
    }

    /// <summary>
    /// Renders the link tree as a 2-column grid of cards.
    /// This is the single LinkTree rendering entry point — there are no other variants.
    /// </summary>
    private void RenderLinkList(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        var visibleNodes = tree.GetVisibleNodes().ToList();
        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var startRow = context.ScrollOffset;

        var linesUsed = 0;
        var rowsRendered = 0;

        // Scroll-up indicator
        if (startRow > 0)
        {
            RenderScrollIndicator(layout.Width, p, true, startRow);
            linesUsed++;
        }

        for (var row = startRow; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];
            var groupCardHeight = layout.CellHeight >= 5 ? 3 : layout.CellHeight;
            var linesNeeded = gr.IsGroupHeader ? GetLinesForNode(gr.Left, groupCardHeight) : layout.CellHeight;

            // Reserve 1 line for scroll-down indicator if more rows follow
            var hasMoreAfter = row + 1 < gridRows.Count;
            var available = hasMoreAfter ? maxLines - 1 : maxLines;

            if (linesUsed + linesNeeded > available)
            {
                break;
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
                RenderGridRow(gr, layout, p, options.CachedUrls, tree.SelectedNodeIds, context.SearchQuery);
            }

            linesUsed += linesNeeded;
            rowsRendered++;
        }

        // Scroll-down indicator
        var totalRemaining = Math.Max(0, gridRows.Count - startRow - rowsRendered);
        if (totalRemaining > 0)
        {
            RenderScrollIndicator(layout.Width, p, false, totalRemaining);
        }
    }

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

    private void RenderGridRow(GridRow row, LinkTreeLayout layout, ThemePalette p, IReadOnlySet<string>? cachedUrls = null, HashSet<Guid>? selectedIds = null, string? searchQuery = null)
    {
        for (var lineIdx = 0; lineIdx < layout.CellHeight; lineIdx++)
        {
            var sb = new StringBuilder();
            var isSeparatorLine = lineIdx == layout.CellHeight - 1 && layout.CellHeight > 1;

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
                    sb.Append(BuildCardLine(node, node.IsSelected, layout.CellHeight, lineIdx, cellW, p, cachedUrls, toggled, searchQuery));
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
