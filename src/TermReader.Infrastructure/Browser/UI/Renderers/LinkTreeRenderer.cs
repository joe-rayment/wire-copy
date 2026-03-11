// Educational and personal use only.

using System.Text;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;

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

    public void RenderHeader(PageMetadata metadata, string url, RenderOptions options)
    {
        var width = Math.Max(1, options.TerminalWidth - 2);
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        var title = metadata.Title ?? "Untitled";
        var domain = LauncherRenderer.ExtractDomain(url);
        var pad = Math.Max(1, width - title.Length - domain.Length - 2);

        if (title.Length + domain.Length + 2 > width)
        {
            title = RenderHelpers.TruncateText(title, Math.Max(10, width - domain.Length - 3));
            pad = Math.Max(1, width - title.Length - domain.Length - 2);
        }

        _helpers.WriteLine(
            $" {p.HeaderTitleFg.AnsiFg}{Bold}{title}{Reset}" +
            $"{new string(' ', pad)}{p.HeaderUrlFg.AnsiFg}{Dim}{domain}{Reset} ");
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}{new string('\u2500', width)}{Reset}");
    }

    public void RenderLinkTree(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        tree.EnsureSelection();

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
                RenderGroupHeader(gr.Left, gr.Left.IsSelected, options);
            }
            else
            {
                RenderGridRow(gr, layout, p, options.CachedUrls);
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
                LinkType.Content => p.LinkContent.AnsiFg,
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
    public void RenderGroupHeader(LinkNode node, bool isSelected, RenderOptions options)
    {
        if (node.Link.HeaderType == HeaderType.SubSection)
        {
            RenderSubSectionHeader(node, isSelected, options);
            return;
        }

        RenderTopLevelGroupHeader(node, isSelected, options);
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
        const int headerLines = 2;
        const int statusBarLines = 3;
        const int columnThreshold = 50;
        const int standardCellHeight = 5;
        const int compactCellHeight = 3;

        var width = Math.Max(1, terminalWidth - 2);
        var availableHeight = Math.Max(4, terminalHeight - headerLines - statusBarLines);
        var columns = width >= columnThreshold ? 2 : 1;
        var cellHeight = availableHeight < 15 ? compactCellHeight : standardCellHeight;
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
        IReadOnlySet<string>? cachedUrls = null)
    {
        return isSelected
            ? BuildSelectedCardLine(node, cardHeight, lineIndex, width, palette)
            : BuildNormalCardLine(node, cardHeight, lineIndex, width, palette, cachedUrls);
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

    private static string BuildSelectedCardLine(
        LinkNode node,
        int cardHeight,
        int lineIndex,
        int width,
        ThemePalette palette)
    {
        var sb = new StringBuilder();
        var accentFg = palette.HeaderBorderFg.AnsiFg;
        var selBg = palette.SelectedItemBg.AnsiBg;
        var selFg = palette.SelectedItemFg.AnsiFg;
        var contentWidth = width - 1;

        // Accent bar on all lines
        sb.Append($"{accentFg}\u258c{Reset}");

        var titleLineIdx = cardHeight >= 5 ? 1 : 0;
        var authorDateLineIdx = cardHeight >= 5 ? 2 : -1;
        var metadataLineIdx = cardHeight >= 5 ? -1 : GetMetadataLineIndex(cardHeight);

        if (lineIndex == titleLineIdx)
        {
            var titleLine = RenderHelpers.TruncateText(node.Link.DisplayText, contentWidth - 1);
            sb.Append($"{selBg}{selFg}{Bold} {titleLine}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - titleLine.Length))}{Reset}");
        }
        else if (lineIndex == authorDateLineIdx || lineIndex == metadataLineIdx)
        {
            var subtitle = GetMetadataSubtitle(node, contentWidth - 1);
            sb.Append($"{selBg}{palette.SecondaryText.AnsiFg} {subtitle}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - subtitle.Length))}{Reset}");
        }
        else if (lineIndex == cardHeight - 1 && cardHeight > 1)
        {
            sb.Append($"{selBg}{palette.HeaderBorderFg.AnsiFg}{new string('\u2500', contentWidth)}{Reset}");
        }
        else
        {
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
        IReadOnlySet<string>? cachedUrls = null)
    {
        var titleLineIdx = cardHeight >= 5 ? 1 : 0;
        var authorDateLineIdx = cardHeight >= 5 ? 2 : -1;
        var metadataLineIdx = cardHeight >= 5 ? -1 : GetMetadataLineIndex(cardHeight);

        if (lineIndex == titleLineIdx)
        {
            var colorFg = node.Link.Type switch
            {
                LinkType.Content => palette.LinkContent.AnsiFg,
                LinkType.Navigation => palette.LinkNavigation.AnsiFg,
                LinkType.External => palette.LinkExternal.AnsiFg,
                LinkType.Footer => palette.LinkFooter.AnsiFg,
                _ => palette.PrimaryText.AnsiFg
            };
            var titleLine = RenderHelpers.TruncateText(node.Link.DisplayText, width - 1);

            var isCached = cachedUrls != null &&
                           !string.IsNullOrEmpty(node.Link.Url) &&
                           cachedUrls.Contains(node.Link.Url);
            var prefix = isCached ? $"{palette.SecondaryText.AnsiFg}\u25cf{Reset}" : " ";
            var pad = new string(' ', Math.Max(0, width - 1 - titleLine.Length));
            return $"{prefix}{colorFg}{titleLine}{pad}{Reset}";
        }

        if (lineIndex == authorDateLineIdx)
        {
            var subtitle = GetMetadataSubtitle(node, width - 1);
            var metaPad = new string(' ', Math.Max(0, width - 1 - subtitle.Length));
            return $" {palette.SecondaryText.AnsiFg}{Dim}{subtitle}{metaPad}{Reset}";
        }

        if (lineIndex == metadataLineIdx)
        {
            var subtitle = GetMetadataSubtitle(node, width - 1);
            var metaPad = new string(' ', Math.Max(0, width - 1 - subtitle.Length));
            return $" {palette.SecondaryText.AnsiFg}{Dim}{subtitle}{metaPad}{Reset}";
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
    /// Renders a sub-section header with visually lighter style than top-level groups.
    /// Standard mode (cardHeight >= 2): blank line + thin-rule title line.
    /// Compact mode (cardHeight == 1): single thin-rule title line.
    /// Format: ' ─ Title (count) ────────'
    /// </summary>
    private void RenderSubSectionHeader(LinkNode node, bool isSelected, RenderOptions options)
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
        var headerLabel = $"\u2500 {collapseIndicator} {node.Link.DisplayText}{countSuffix} ";

        // Fill remaining width with thin rule
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
    private void RenderTopLevelGroupHeader(LinkNode node, bool isSelected, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Max(1, options.TerminalWidth - 2);
        var availableLines = Math.Max(3, options.TerminalHeight - 9);
        var cardHeight = GetCardHeight(availableLines);
        var isExpanded = node.CollapseState == NodeCollapseState.Expanded;
        var collapseIndicator = isExpanded ? "\u25bc" : "\u25b6";
        var childCount = node.Children.Count;
        var headerText = $"{collapseIndicator} {node.Link.DisplayText.ToUpperInvariant()} ({childCount})";

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

        // Line 2: thin rule (only for expanded groups in standard+ mode)
        if (isExpanded)
        {
            var ruleWidth = Math.Max(1, width - 2);
            if (isSelected)
            {
                _helpers.WriteLine(
                    $"{p.HeaderBorderFg.AnsiFg}\u258c{Reset}" +
                    $"{p.SelectedItemBg.AnsiBg} {p.HeaderBorderFg.AnsiFg}{new string('\u2500', ruleWidth)}{Reset}");
            }
            else
            {
                _helpers.WriteLine($" {p.HeaderBorderFg.AnsiFg}{new string('\u2500', ruleWidth)}{Reset}");
            }
        }
    }

    private void RenderGridRow(GridRow row, LinkTreeLayout layout, ThemePalette p, IReadOnlySet<string>? cachedUrls = null)
    {
        for (var lineIdx = 0; lineIdx < layout.CellHeight; lineIdx++)
        {
            var sb = new StringBuilder();
            sb.Append(BuildCardLine(row.Left, row.Left.IsSelected, layout.CellHeight, lineIdx, layout.CellWidth, p, cachedUrls));

            if (layout.Columns == 2)
            {
                var isSeparatorLine = lineIdx == layout.CellHeight - 1 && layout.CellHeight > 1;
                var divider = isSeparatorLine ? "\u253c" : "\u2502";
                sb.Append($"{p.SecondaryText.AnsiFg}{divider}{Reset}");
                if (row.Right != null)
                {
                    var rightWidth = layout.Width - layout.CellWidth - 1;
                    sb.Append(BuildCardLine(row.Right, row.Right.IsSelected, layout.CellHeight, lineIdx, rightWidth, p, cachedUrls));
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
}
