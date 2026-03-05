// Educational and personal use only.

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
    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public LinkTreeRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    public void RenderHeader(PageMetadata metadata, string url, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);
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

        var visibleNodes = tree.GetVisibleNodes().ToList();
        var startIndex = context.ScrollOffset;

        // Use a line-budget loop instead of node-count loop because
        // depth-1 group headers consume 2 lines (blank line + header text).
        var linesUsed = 0;
        var nodesRendered = 0;
        var reserveForMore = 1; // Reserve 1 line for "... N more" indicator

        for (var i = startIndex; i < visibleNodes.Count; i++)
        {
            var node = visibleNodes[i];
            var linesNeeded = (node.IsGroupHeader && node.Depth == 1) ? 2 : 1;

            // Check if we have room (reserve space for "more" indicator if there are remaining nodes)
            var hasMoreAfter = i + 1 < visibleNodes.Count;
            var available = hasMoreAfter ? maxLines - reserveForMore : maxLines;

            if (linesUsed + linesNeeded > available)
            {
                break;
            }

            RenderLinkNode(node, node.IsSelected, options);
            linesUsed += linesNeeded;
            nodesRendered++;
        }

        var totalRemaining = Math.Max(0, visibleNodes.Count - startIndex - nodesRendered);
        if (totalRemaining > 0)
        {
            var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
            _helpers.WriteLine($"{p.SecondaryText.AnsiFg}  ... {totalRemaining} more links (scroll with j/k){Reset}");
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

        var collapseIndicator = node.Children.Count > 0
            ? (node.CollapseState == NodeCollapseState.Expanded ? "\u25bc" : "\u25b6")
            : " ";

        var displayText = RenderHelpers.TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);
        _helpers.WriteLine($"{colorStart}{indent}{prefix}{collapseIndicator} {displayText}{Reset}");
    }

    public void RenderGroupHeader(LinkNode node, bool isSelected, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var indent = new string(' ', (node.Depth * 2) + 2);
        var prefix = isSelected ? "\u2192" : " ";

        var collapseIndicator = node.CollapseState == NodeCollapseState.Expanded ? "\u25bc" : "\u25b6";

        var colorStart = isSelected
            ? $"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg}"
            : p.PrimaryText.AnsiFg;

        var displayText = RenderHelpers.TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);

        if (node.Depth == 1)
        {
            _helpers.WriteLine();
        }

        _helpers.WriteLine($"{colorStart}{indent}{prefix}{collapseIndicator} {displayText}{Reset}");
    }
}
