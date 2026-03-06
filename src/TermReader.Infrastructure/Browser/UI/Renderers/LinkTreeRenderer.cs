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
        var border = p.HeaderBorderFg.AnsiFg;
        var titleFg = p.HeaderTitleFg.AnsiFg;
        var urlFg = p.HeaderUrlFg.AnsiFg;

        _helpers.WriteLine();
        _helpers.WriteLine($"{border}\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557{Reset}");

        var title = RenderHelpers.TruncateText(metadata.Title ?? "Untitled", width - 4);
        _helpers.WriteLine($"{border}\u2551 {titleFg}{title.PadRight(width - 4)}{border} \u2551{Reset}");

        var displayUrl = RenderHelpers.TruncateUrl(url, width - 4);
        _helpers.WriteLine($"{border}\u2551 {urlFg}{displayUrl.PadRight(width - 4)}{border} \u2551{Reset}");

        _helpers.WriteLine($"{border}\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d{Reset}");
        _helpers.WriteLine();
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
                RenderGridRow(gr, layout, p);
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
    /// Renders a card-style group header.
    /// Standard mode (cardHeight >= 2): blank line + header text [+ thin rule for expanded].
    /// Compact mode (cardHeight == 1): single line.
    /// Selected headers get accent bar and highlight background.
    /// </summary>
    public void RenderGroupHeader(LinkNode node, bool isSelected, RenderOptions options)
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
        const int headerLines = 6;
        const int statusBarLines = 3;
        const int columnThreshold = 50;
        const int standardCellHeight = 5;
        const int compactCellHeight = 3;

        var width = Math.Max(1, terminalWidth - 2);
        var availableHeight = Math.Max(4, terminalHeight - headerLines - statusBarLines);
        var columns = width >= columnThreshold ? 2 : 1;
        var cellHeight = availableHeight < 15 ? compactCellHeight : standardCellHeight;
        var visibleRows = Math.Max(1, availableHeight / cellHeight);
        var cellWidth = columns == 1 ? width : (width - 1) / 2;

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
        ThemePalette palette)
    {
        return isSelected
            ? BuildSelectedCardLine(node, cardHeight, lineIndex, width, palette)
            : BuildNormalCardLine(node, cardHeight, lineIndex, width, palette);
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
        var metadataLineIdx = GetMetadataLineIndex(cardHeight);

        if (lineIndex == titleLineIdx)
        {
            var title = RenderHelpers.TruncateText(node.Link.DisplayText, contentWidth - 1);
            sb.Append($"{selBg}{selFg}{Bold} {title}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - title.Length))}{Reset}");
        }
        else if (lineIndex == metadataLineIdx)
        {
            var subtitle = GetMetadataSubtitle(node, contentWidth - 1);
            sb.Append($"{selBg}\x1b[38;5;245m {subtitle}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - 1 - subtitle.Length))}{Reset}");
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
        ThemePalette palette)
    {
        var titleLineIdx = cardHeight >= 5 ? 1 : 0;
        var metadataLineIdx = GetMetadataLineIndex(cardHeight);

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
            var title = RenderHelpers.TruncateText(node.Link.DisplayText, width - 1);
            return $" {colorFg}{title}{Reset}";
        }

        if (lineIndex == metadataLineIdx)
        {
            var subtitle = GetMetadataSubtitle(node, width - 1);
            return $" {palette.SecondaryText.AnsiFg}{Dim}{subtitle}{Reset}";
        }

        return string.Empty;
    }

    private static string GetMetadataSubtitle(LinkNode node, int maxWidth)
    {
        var dateStr = FormatDate(node.Link.PublishedDate);
        var author = node.Link.Author;
        var domain = LauncherRenderer.ExtractDomain(node.Link.Url);

        string subtitle;
        if (dateStr != null && !string.IsNullOrEmpty(author))
        {
            subtitle = $"{dateStr} \u00b7 {author}";
        }
        else if (dateStr != null)
        {
            subtitle = $"{dateStr} \u00b7 {domain}";
        }
        else if (!string.IsNullOrEmpty(author))
        {
            subtitle = author;
        }
        else
        {
            subtitle = domain;
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

        if (cardHeight == 1)
        {
            return 1;
        }

        return node.CollapseState == NodeCollapseState.Expanded ? 3 : 2;
    }

    private void RenderGridRow(GridRow row, LinkTreeLayout layout, ThemePalette p)
    {
        for (var lineIdx = 0; lineIdx < layout.CellHeight; lineIdx++)
        {
            var sb = new StringBuilder();
            sb.Append(BuildCardLine(row.Left, row.Left.IsSelected, layout.CellHeight, lineIdx, layout.CellWidth, p));

            if (layout.Columns == 2)
            {
                sb.Append($"{p.SecondaryText.AnsiFg}\u2502{Reset}");
                if (row.Right != null)
                {
                    var rightWidth = layout.Width - layout.CellWidth - 1;
                    sb.Append(BuildCardLine(row.Right, row.Right.IsSelected, layout.CellHeight, lineIdx, rightWidth, p));
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
