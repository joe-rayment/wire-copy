// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

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
        var boxWidth = Math.Max(title.Length + 6, Math.Min(width - 2, 78));

        // ╭─ Title ──────────────────────────────────────╮
        var titlePad = Math.Max(0, boxWidth - title.Length - 5);
        _helpers.WriteLine(
            $" {borderColor}\u256d\u2500 {Reset}{p.PrimaryText.AnsiFg}{Bold}{title}{Reset}" +
            $" {borderColor}{new string('\u2500', titlePad)}\u256e{Reset}");

        // │ subtitle                                      │
        var innerWidth = boxWidth - 2;
        var subtitlePad = Math.Max(0, innerWidth - subtitle.Length);
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

        var variant = options.LayoutVariant;

        switch (variant)
        {
            case "DenseList":
                RenderDenseList(tree, context, maxLines, options);
                return;
            case "Magazine":
                RenderMagazineList(tree, context, maxLines, options);
                return;
            default:
                RenderCardsLayout(tree, context, maxLines, options);
                return;
        }
    }

    public void RenderLinkNode(LinkNode node, bool isSelected, RenderOptions options)
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
    internal static LinkTreeLayout ComputeLayout(int terminalWidth, int terminalHeight, string? layoutVariant = null)
    {
        const int headerLines = 3;
        const int statusBarLines = 2;

        var width = Math.Max(1, terminalWidth - 2);
        var availableHeight = Math.Max(4, terminalHeight - headerLines - statusBarLines);

        int columns;
        int cellHeight;

        switch (layoutVariant)
        {
            case "DenseList":
                columns = 1;
                cellHeight = 1;
                break;
            case "Magazine":
                columns = 1;
                cellHeight = 2;
                break;
            default: // "Cards" or null
            {
                const int columnThreshold = 50;
                const int standardCellHeight = 5;
                const int compactCellHeight = 3;
                columns = width >= columnThreshold ? 2 : 1;
                cellHeight = availableHeight < 15 ? compactCellHeight : standardCellHeight;
                break;
            }
        }

        var visibleRows = Math.Max(1, availableHeight / cellHeight);
        var cellWidth = Math.Max(1, columns == 1 ? width : (width - 1) / 2);

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
        bool isToggled = false)
    {
        return isSelected
            ? BuildSelectedCardLine(node, cardHeight, lineIndex, width, palette, isToggled)
            : BuildNormalCardLine(node, cardHeight, lineIndex, width, palette, isToggled);
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
    /// Gets a specific wrapped line of the title text (0-indexed).
    /// Returns the requested line, or empty string if the title doesn't have that many lines.
    /// If lineNumber == 1 and there are 3+ wrapped lines, the second line is truncated with ellipsis.
    /// </summary>
    internal static string GetWrappedTitleLine(string displayText, int textWidth, int lineNumber)
    {
        if (textWidth <= 0 || string.IsNullOrEmpty(displayText))
        {
            return lineNumber == 0 ? RenderHelpers.TruncateText(displayText ?? string.Empty, Math.Max(1, textWidth)) : string.Empty;
        }

        var wrapped = RenderHelpers.WrapText(displayText, textWidth);
        if (wrapped.Count == 0)
        {
            return string.Empty;
        }

        if (lineNumber == 0)
        {
            return wrapped[0];
        }

        if (lineNumber == 1 && wrapped.Count > 1)
        {
            // If 3+ wrapped lines, truncate the second line with ellipsis
            return wrapped.Count > 2
                ? RenderHelpers.TruncateText(string.Join(" ", wrapped.Skip(1)), textWidth)
                : wrapped[1];
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets the number of lines a group header occupies at a given card height.
    /// Used by AdjustScrollForSelection to mirror the rendering loop's line accounting.
    /// Sub-section headers are always 2 lines (blank + header) or 1 in compact mode.
    /// Top-level headers are 3 lines when expanded (blank + header + rule) or 2 when collapsed.
    /// </summary>
    internal static int GetLinesForGroupHeader(LinkNode node, int cardHeight)
    {
        return GetLinesForGroupHeader(node, cardHeight, null);
    }

    /// <summary>
    /// Gets the number of lines a group header occupies, respecting the layout variant.
    /// </summary>
    internal static int GetLinesForGroupHeader(LinkNode node, int cardHeight, string? layoutVariant)
    {
        switch (layoutVariant)
        {
            case "DenseList":
                return GetDenseGroupHeaderLines(node);
            case "Magazine":
                return GetMagazineGroupHeaderLines(node);
            default:
                // Cards layout (original behavior)
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
    }

    /// <summary>
    /// Returns how many lines a group header occupies in DenseList mode.
    /// All group headers are a single line in this compact layout.
    /// </summary>
    internal static int GetDenseGroupHeaderLines(LinkNode node)
    {
        // DenseList: always 1 line per group header
        return 1;
    }

    /// <summary>
    /// Returns how many lines a group header occupies in Magazine mode.
    /// Top-level expanded headers get 2 lines (blank + header), collapsed get 1.
    /// Sub-section headers get 1 line.
    /// </summary>
    internal static int GetMagazineGroupHeaderLines(LinkNode node)
    {
        if (node.Link.HeaderType == HeaderType.SubSection)
        {
            return 1;
        }

        // Top-level: expanded gets 2 lines (blank + header), collapsed gets 1
        return node.CollapseState == NodeCollapseState.Expanded ? 2 : 1;
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
        bool isToggled = false)
    {
        var sb = new StringBuilder();
        var accentFg = palette.HeaderBorderFg.AnsiFg;
        var selBg = palette.SelectedItemBg.AnsiBg;
        var selFg = palette.SelectedItemFg.AnsiFg;
        var contentWidth = width - 1;

        var titleLineIdx = cardHeight >= 5 ? 1 : 0;
        var titleLine2Idx = cardHeight >= 5 ? 2 : -1;
        var authorDateLineIdx = cardHeight >= 5 ? 3 : -1;
        var metadataLineIdx = cardHeight >= 5 ? -1 : GetMetadataLineIndex(cardHeight);
        var isSeparator = lineIndex == cardHeight - 1 && cardHeight > 1;

        // Determine the content block range: titleLineIdx through last content slot.
        // All lines in this range get accent bar + background, creating a consistent
        // rectangular highlight regardless of whether individual lines have text.
        var lastContentLineIdx = Math.Max(
            titleLineIdx,
            Math.Max(titleLine2Idx, Math.Max(authorDateLineIdx, metadataLineIdx)));
        var isInContentBlock = lineIndex >= titleLineIdx && lineIndex <= lastContentLineIdx;

        // Accent bar on content block lines; ✓ on title line when toggled
        if (isInContentBlock && isToggled && lineIndex == titleLineIdx)
        {
            sb.Append($"{palette.GetAccentFg().AnsiFg}\u2713{Reset}");
        }
        else
        {
            sb.Append(isInContentBlock ? $"{accentFg}\u258c{Reset}" : " ");
        }

        if (lineIndex == titleLineIdx)
        {
            var textWidth = GetTitleTextWidth(width);
            var titleLine = cardHeight >= 5
                ? GetWrappedTitleLine(node.Link.DisplayText, textWidth, 0)
                : RenderHelpers.TruncateText(node.Link.DisplayText, contentWidth - 1);

            sb.Append($"{selBg}{selFg}{Bold} {titleLine}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - titleLine.Length))}{Reset}");
        }
        else if (lineIndex == titleLine2Idx)
        {
            var textWidth = GetTitleTextWidth(width);
            var titleLine2 = GetWrappedTitleLine(node.Link.DisplayText, textWidth, 1);
            if (!string.IsNullOrEmpty(titleLine2))
            {
                sb.Append($"{selBg}{selFg}{Bold} {titleLine2}");
                sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - titleLine2.Length))}{Reset}");
            }
            else
            {
                sb.Append($"{selBg}{new string(' ', contentWidth)}{Reset}");
            }
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
        else if (isSeparator)
        {
            // Separator line — no highlight, matches unselected appearance
            sb.Append($"{palette.HeaderBorderFg.AnsiFg}{new string('\u2500', contentWidth)}{Reset}");
        }
        else
        {
            // Top padding — no highlight
            sb.Append($"{new string(' ', contentWidth)}");
        }

        return sb.ToString();
    }

    private static string BuildNormalCardLine(
        LinkNode node,
        int cardHeight,
        int lineIndex,
        int width,
        ThemePalette palette,
        bool isToggled = false)
    {
        var titleLineIdx = cardHeight >= 5 ? 1 : 0;
        var titleLine2Idx = cardHeight >= 5 ? 2 : -1;
        var authorDateLineIdx = cardHeight >= 5 ? 3 : -1;
        var metadataLineIdx = cardHeight >= 5 ? -1 : GetMetadataLineIndex(cardHeight);

        if (lineIndex == titleLineIdx)
        {
            var colorFg = node.Link.Type switch
            {
                LinkType.Content => palette.HeaderTitleFg.AnsiFg,
                LinkType.Navigation => palette.LinkNavigation.AnsiFg,
                LinkType.External => palette.LinkExternal.AnsiFg,
                LinkType.Footer => palette.LinkFooter.AnsiFg,
                _ => palette.PrimaryText.AnsiFg
            };
            var textWidth = GetTitleTextWidth(width);
            var titleLine = cardHeight >= 5
                ? GetWrappedTitleLine(node.Link.DisplayText, textWidth, 0)
                : RenderHelpers.TruncateText(node.Link.DisplayText, width - 1);

            var prefix = isToggled ? $"{palette.GetAccentFg().AnsiFg}\u2713{Reset}" : " ";
            var pad = new string(' ', Math.Max(0, width - 1 - titleLine.Length));
            return $"{prefix}{colorFg}{titleLine}{pad}{Reset}";
        }

        if (lineIndex == titleLine2Idx)
        {
            var textWidth = GetTitleTextWidth(width);
            var titleLine2 = GetWrappedTitleLine(node.Link.DisplayText, textWidth, 1);
            if (!string.IsNullOrEmpty(titleLine2))
            {
                var colorFg = node.Link.Type switch
                {
                    LinkType.Content => palette.HeaderTitleFg.AnsiFg,
                    LinkType.Navigation => palette.LinkNavigation.AnsiFg,
                    LinkType.External => palette.LinkExternal.AnsiFg,
                    LinkType.Footer => palette.LinkFooter.AnsiFg,
                    _ => palette.PrimaryText.AnsiFg
                };
                var pad = new string(' ', Math.Max(0, width - 1 - titleLine2.Length));
                return $" {colorFg}{titleLine2}{pad}{Reset}";
            }

            return new string(' ', width);
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
            return $"{palette.SecondaryText.AnsiFg}{Dim}{new string('\u2500', width)}{Reset}";
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

    private static int GetMetadataLineIndex(int cardHeight)
    {
        if (cardHeight >= 5)
        {
            return 3;
        }

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
    /// Renders the Cards layout (original 2-column grid with 5-line cells).
    /// </summary>
    private void RenderCardsLayout(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, "Cards");
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
                RenderGridRow(gr, layout, p, options.CachedUrls, tree.SelectedNodeIds);
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
    /// Renders the DenseList layout: single-column, 1 line per link with right-aligned domain.
    /// </summary>
    private void RenderDenseList(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, "DenseList");
        var visibleNodes = tree.GetVisibleNodes().ToList();
        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var startRow = context.ScrollOffset;

        var linesUsed = 0;
        var rowsRendered = 0;

        if (startRow > 0)
        {
            RenderScrollIndicator(layout.Width, p, true, startRow);
            linesUsed++;
        }

        for (var row = startRow; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];
            var linesNeeded = gr.IsGroupHeader ? GetDenseGroupHeaderLines(gr.Left) : 1;

            var hasMoreAfter = row + 1 < gridRows.Count;
            var available = hasMoreAfter ? maxLines - 1 : maxLines;

            if (linesUsed + linesNeeded > available)
            {
                break;
            }

            if (gr.IsGroupHeader)
            {
                RenderDenseGroupHeader(gr.Left, gr.Left.IsSelected, layout.Width, p, tree);
            }
            else
            {
                var isToggled = tree.SelectedNodeIds != null && tree.SelectedNodeIds.Contains(gr.Left.Id);
                RenderDenseListRow(gr.Left, layout.Width, p, isToggled);
            }

            linesUsed += linesNeeded;
            rowsRendered++;
        }

        var totalRemaining = Math.Max(0, gridRows.Count - startRow - rowsRendered);
        if (totalRemaining > 0)
        {
            RenderScrollIndicator(layout.Width, p, false, totalRemaining);
        }
    }

    /// <summary>
    /// Renders the Magazine layout: single-column, 2 lines per link (title + metadata).
    /// </summary>
    private void RenderMagazineList(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, "Magazine");
        var visibleNodes = tree.GetVisibleNodes().ToList();
        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var startRow = context.ScrollOffset;

        var linesUsed = 0;
        var rowsRendered = 0;

        if (startRow > 0)
        {
            RenderScrollIndicator(layout.Width, p, true, startRow);
            linesUsed++;
        }

        for (var row = startRow; row < gridRows.Count; row++)
        {
            var gr = gridRows[row];
            var linesNeeded = gr.IsGroupHeader ? GetMagazineGroupHeaderLines(gr.Left) : 2;

            var hasMoreAfter = row + 1 < gridRows.Count;
            var available = hasMoreAfter ? maxLines - 1 : maxLines;

            if (linesUsed + linesNeeded > available)
            {
                break;
            }

            if (gr.IsGroupHeader)
            {
                RenderMagazineGroupHeader(gr.Left, gr.Left.IsSelected, layout.Width, p, tree);
            }
            else
            {
                var isToggled = tree.SelectedNodeIds != null && tree.SelectedNodeIds.Contains(gr.Left.Id);
                RenderMagazineRow(gr.Left, layout.Width, p, isToggled);
            }

            linesUsed += linesNeeded;
            rowsRendered++;
        }

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

    private void RenderGridRow(GridRow row, LinkTreeLayout layout, ThemePalette p, IReadOnlySet<string>? cachedUrls = null, HashSet<Guid>? selectedIds = null)
    {
        for (var lineIdx = 0; lineIdx < layout.CellHeight; lineIdx++)
        {
            var sb = new StringBuilder();
            var leftToggled = selectedIds != null && selectedIds.Contains(row.Left.Id);
            sb.Append(BuildCardLine(row.Left, row.Left.IsSelected, layout.CellHeight, lineIdx, layout.CellWidth, p, cachedUrls, leftToggled));

            if (layout.Columns == 2)
            {
                var isSeparatorLine = lineIdx == layout.CellHeight - 1 && layout.CellHeight > 1;
                var divider = isSeparatorLine ? "\u253c" : "\u2502";
                sb.Append($"{p.SecondaryText.AnsiFg}{divider}{Reset}");
                if (row.Right != null)
                {
                    var rightToggled = selectedIds != null && selectedIds.Contains(row.Right.Id);
                    var rightWidth = layout.Width - layout.CellWidth - 1;
                    sb.Append(BuildCardLine(row.Right, row.Right.IsSelected, layout.CellHeight, lineIdx, rightWidth, p, cachedUrls, rightToggled));
                }
                else
                {
                    sb.Append(new string(' ', layout.Width - layout.CellWidth - 1));
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

    // ── DenseList layout helpers ──────────────────────────────────────────

    /// <summary>
    /// Renders a group header in DenseList mode: single line, ALL CAPS, with collapse indicator.
    /// </summary>
    private void RenderDenseGroupHeader(LinkNode node, bool isSelected, int width, ThemePalette p, NavigationTree tree)
    {
        var isExpanded = node.CollapseState == NodeCollapseState.Expanded;
        var collapseIndicator = isExpanded ? "\u25bc" : "\u25b6";
        var childCount = node.Children.Count;

        var selIndicator = string.Empty;
        if (tree.IsSectionFullySelected(node))
        {
            selIndicator = "\u2713 ";
        }
        else if (tree.IsSectionPartiallySelected(node))
        {
            selIndicator = "\u25d0 ";
        }

        string headerText;
        if (node.Link.HeaderType == HeaderType.SubSection)
        {
            var showCount = !isExpanded || childCount >= 5;
            var countSuffix = showCount ? $" ({childCount})" : string.Empty;
            headerText = $"{selIndicator}{collapseIndicator} {node.Link.DisplayText}{countSuffix}";
        }
        else
        {
            headerText = $"{selIndicator}{collapseIndicator} {node.Link.DisplayText.ToUpperInvariant()} ({childCount})";
        }

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
    }

    /// <summary>
    /// Renders a single link row in DenseList mode: title left-aligned, domain right-aligned.
    /// </summary>
    private void RenderDenseListRow(LinkNode node, int width, ThemePalette p, bool isToggled)
    {
        var isSelected = node.IsSelected;
        var domain = LauncherRenderer.ExtractDomain(node.Link.Url);

        // Layout: " title          domain " or "▌ title          domain "
        // Reserve: 2 chars left (accent bar + space or space + space), 1 char right padding, domain length, 2 chars gap
        var domainLen = domain.Length;
        var maxTitleLen = Math.Max(1, width - domainLen - 5);
        var truncTitle = RenderHelpers.TruncateText(node.Link.DisplayText, maxTitleLen);
        var gap = Math.Max(2, width - 2 - truncTitle.Length - domainLen);

        if (isSelected)
        {
            var sb = new StringBuilder();
            if (isToggled)
            {
                sb.Append($"{p.GetAccentFg().AnsiFg}\u2713{Reset}");
            }
            else
            {
                sb.Append($"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}");
            }

            sb.Append($"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg} {truncTitle}");
            sb.Append($"{new string(' ', gap)}");
            sb.Append($"{p.SecondaryText.AnsiFg}{domain}");
            var totalContent = 1 + truncTitle.Length + gap + domainLen;
            sb.Append($"{new string(' ', Math.Max(0, width - totalContent))}{Reset}");
            _helpers.WriteLine(sb.ToString());
        }
        else
        {
            var colorFg = node.Link.Type switch
            {
                LinkType.Content => p.HeaderTitleFg.AnsiFg,
                LinkType.Navigation => p.LinkNavigation.AnsiFg,
                LinkType.External => p.LinkExternal.AnsiFg,
                LinkType.Footer => p.LinkFooter.AnsiFg,
                _ => p.PrimaryText.AnsiFg
            };
            var prefix = isToggled ? $"{p.GetAccentFg().AnsiFg}\u2713{Reset}" : " ";
            var sb = new StringBuilder();
            sb.Append($"{prefix}{colorFg} {truncTitle}{Reset}");
            sb.Append(new string(' ', gap));
            sb.Append($"{p.SecondaryText.AnsiFg}{domain}{Reset}");
            _helpers.WriteLine(sb.ToString());
        }
    }

    // ── Magazine layout helpers ──────────────────────────────────────────

    /// <summary>
    /// Renders a group header in Magazine mode.
    /// </summary>
    private void RenderMagazineGroupHeader(LinkNode node, bool isSelected, int width, ThemePalette p, NavigationTree tree)
    {
        var isExpanded = node.CollapseState == NodeCollapseState.Expanded;
        var collapseIndicator = isExpanded ? "\u25bc" : "\u25b6";
        var childCount = node.Children.Count;

        var selIndicator = string.Empty;
        if (tree.IsSectionFullySelected(node))
        {
            selIndicator = "\u2713 ";
        }
        else if (tree.IsSectionPartiallySelected(node))
        {
            selIndicator = "\u25d0 ";
        }

        if (node.Link.HeaderType == HeaderType.SubSection)
        {
            // Sub-section: single compact line with thin rule
            var showCount = !isExpanded || childCount >= 5;
            var countSuffix = showCount ? $" ({childCount})" : string.Empty;
            var titleText = $"{selIndicator}{collapseIndicator} {node.Link.DisplayText}{countSuffix}";
            var headerLabel = $" \u2500 {titleText} ";
            var ruleLen = Math.Max(0, width - headerLabel.Length - 1);
            var headerLine = $"{headerLabel}{new string('\u2500', ruleLen)}";
            var truncLine = RenderHelpers.TruncateText(headerLine, width - 1);

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

            return;
        }

        // Top-level header
        var headerText = $"{selIndicator}{collapseIndicator} {node.Link.DisplayText.ToUpperInvariant()} ({childCount})";
        var truncHeader = RenderHelpers.TruncateText(headerText, width - 2);

        if (isExpanded)
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

        // Header text line
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
    }

    /// <summary>
    /// Renders a link row in Magazine mode: line 1 = title (bold), line 2 = author/date/domain.
    /// Selection highlights both lines.
    /// </summary>
    private void RenderMagazineRow(LinkNode node, int width, ThemePalette p, bool isToggled)
    {
        var isSelected = node.IsSelected;
        var domain = LauncherRenderer.ExtractDomain(node.Link.Url);
        var titleMaxWidth = Math.Max(1, width - 2);
        var truncTitle = RenderHelpers.TruncateText(node.Link.DisplayText, titleMaxWidth);

        // Build metadata line: author · date · domain
        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(node.Link.Author))
        {
            metaParts.Add(node.Link.Author);
        }

        var dateStr = FormatDate(node.Link.PublishedDate);
        if (dateStr != null)
        {
            metaParts.Add(dateStr);
        }

        metaParts.Add(domain);
        var metaText = string.Join(" \u00b7 ", metaParts);
        var metaMaxWidth = Math.Max(1, width - 4); // 2 extra indent
        var truncMeta = RenderHelpers.TruncateText(metaText, metaMaxWidth);

        if (isSelected)
        {
            // Line 1: title with accent bar and highlight
            var sb1 = new StringBuilder();
            if (isToggled)
            {
                sb1.Append($"{p.GetAccentFg().AnsiFg}\u2713{Reset}");
            }
            else
            {
                sb1.Append($"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}");
            }

            sb1.Append($"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg}{Bold} {truncTitle}");
            sb1.Append($"{new string(' ', Math.Max(0, width - 1 - truncTitle.Length))}{Reset}");
            _helpers.WriteLine(sb1.ToString());

            // Line 2: metadata with accent bar and highlight
            var sb2 = new StringBuilder();
            sb2.Append($"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}");
            sb2.Append($"{p.SelectedItemBg.AnsiBg}{p.SecondaryText.AnsiFg}   {truncMeta}");
            sb2.Append($"{new string(' ', Math.Max(0, width - 3 - truncMeta.Length))}{Reset}");
            _helpers.WriteLine(sb2.ToString());
        }
        else
        {
            // Line 1: title
            var colorFg = node.Link.Type switch
            {
                LinkType.Content => p.HeaderTitleFg.AnsiFg,
                LinkType.Navigation => p.LinkNavigation.AnsiFg,
                LinkType.External => p.LinkExternal.AnsiFg,
                LinkType.Footer => p.LinkFooter.AnsiFg,
                _ => p.PrimaryText.AnsiFg
            };
            var prefix = isToggled ? $"{p.GetAccentFg().AnsiFg}\u2713{Reset}" : " ";
            _helpers.WriteLine($"{prefix}{colorFg}{Bold} {truncTitle}{Reset}");

            // Line 2: metadata (indented 2 extra)
            _helpers.WriteLine($"    {p.SecondaryText.AnsiFg}{truncMeta}{Reset}");
        }
    }
}
