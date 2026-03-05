// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the hierarchical link tree view including headers, nodes, and group headers.
/// </summary>
internal class LinkTreeRenderer
{
    private readonly RenderHelpers _helpers;

    public LinkTreeRenderer(RenderHelpers helpers)
    {
        _helpers = helpers;
    }

    public void RenderHeader(PageMetadata metadata, string url, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        _helpers.WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        var title = RenderHelpers.TruncateText(metadata.Title ?? "Untitled", width - 4);
        _helpers.WriteLine($"\u2551 {title.PadRight(width - 4)} \u2551");

        var displayUrl = RenderHelpers.TruncateUrl(url, width - 4);
        var urlLine = $"\x1b[90m\u2551 \x1b[37m{displayUrl.PadRight(width - 4)}\x1b[36m \u2551";
        _helpers.WriteLine(urlLine);

        _helpers.WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();
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
            Console.ForegroundColor = ConsoleColor.DarkGray;
            _helpers.WriteLine($"  ... {totalRemaining} more links (scroll with j/k)");
            Console.ResetColor();
        }
    }

    public void RenderLinkNode(LinkNode node, bool isSelected, RenderOptions options)
    {
        if (node.IsGroupHeader)
        {
            RenderGroupHeader(node, isSelected, options);
            return;
        }

        var indent = new string(' ', (node.Depth * 2) + 4);
        var prefix = isSelected ? "\u2192" : " ";

        if (isSelected)
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
        }
        else
        {
            Console.ForegroundColor = node.Link.Type switch
            {
                LinkType.Content => ConsoleColor.Green,
                LinkType.Navigation => ConsoleColor.Gray,
                LinkType.External => ConsoleColor.Cyan,
                LinkType.Footer => ConsoleColor.DarkGray,
                _ => ConsoleColor.White
            };
        }

        var collapseIndicator = node.Children.Count > 0
            ? (node.CollapseState == NodeCollapseState.Expanded ? "\u25bc" : "\u25b6")
            : " ";

        var displayText = RenderHelpers.TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);
        _helpers.WriteLine($"{indent}{prefix}{collapseIndicator} {displayText}");

        Console.ResetColor();
    }

    public void RenderGroupHeader(LinkNode node, bool isSelected, RenderOptions options)
    {
        var indent = new string(' ', (node.Depth * 2) + 2);
        var prefix = isSelected ? "\u2192" : " ";

        var collapseIndicator = node.CollapseState == NodeCollapseState.Expanded ? "\u25bc" : "\u25b6";

        if (isSelected)
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.White;
        }

        var displayText = RenderHelpers.TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);

        if (node.Depth == 1)
        {
            _helpers.WriteLine();
        }

        _helpers.WriteLine($"{indent}{prefix}{collapseIndicator} {displayText}");

        Console.ResetColor();
    }
}
