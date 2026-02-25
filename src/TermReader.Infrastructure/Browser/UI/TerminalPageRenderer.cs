// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.UI;

/// <summary>
/// Renders pages to the terminal.
/// Handles both hierarchical (link tree) and readable (article) views.
/// Uses ANSI escape sequences for efficient line clearing.
/// </summary>
public class TerminalPageRenderer : IPageRenderer
{
    private readonly ILogger<TerminalPageRenderer> _logger;
    private int _linesWritten;

    public TerminalPageRenderer(ILogger<TerminalPageRenderer> logger)
    {
        _logger = logger;
    }

    public void RenderHierarchical(Page page, NavigationContext context, RenderOptions options)
    {
        Clear();

        // Render header
        RenderHeader(page.Metadata, page.Url, options);

        // Calculate remaining height after header (3 lines for separator + status bar + padding)
        var remainingHeight = Math.Max(3, options.TerminalHeight - _linesWritten - 3);

        // Render link tree
        if (page.LinkTree != null)
        {
            RenderLinkTree(page.LinkTree, context, remainingHeight, options);
        }
        else
        {
            WriteLine();
            WriteLine("  No links found on this page.");
            WriteLine();
        }

        // Render status bar
        RenderStatusBar(context, ViewMode.Hierarchical);

        // Clear any remaining lines from previous render
        ClearRemainingLines();
    }

    public void RenderReadable(Page page, NavigationContext context, RenderOptions options, List<string>? wrappedLines = null)
    {
        Clear();

        if (page.ReadableContent == null)
        {
            WriteLine();
            WriteLine("  No readable content available for this page.");
            WriteLine("  Press 'v' to switch to link view.");
            WriteLine();
            RenderStatusBar(context, ViewMode.Readable);
            ClearRemainingLines();
            return;
        }

        // Render article header
        RenderArticleHeader(page.ReadableContent, options);

        // Calculate remaining height after header (3 lines for separator + status bar + padding)
        var viewportHeight = Math.Max(3, options.TerminalHeight - _linesWritten - 3);

        // Render article content using pre-wrapped lines if available
        if (wrappedLines != null)
        {
            RenderLineBasedContent(wrappedLines, context, viewportHeight, options);
        }
        else
        {
            RenderArticleContent(page.ReadableContent, context, viewportHeight, options);
        }

        // Render status bar
        RenderStatusBar(context, ViewMode.Readable);

        // Clear any remaining lines from previous render
        ClearRemainingLines();
    }

    public void RenderLoading(string url)
    {
        Clear();
        WriteLine();
        WriteLine("  Loading...");
        WriteLine($"  {TruncateUrl(url, 70)}");
        WriteLine();
        ClearRemainingLines();
    }

    public void RenderError(string message, string url)
    {
        Clear();
        WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        WriteLine("  Error loading page:");
        Console.ResetColor();
        WriteLine($"  {message}");
        WriteLine();
        WriteLine($"  URL: {TruncateUrl(url, 60)}");
        WriteLine();
        WriteLine("  Press 'b' to go back or 'q' to quit.");
        WriteLine();
        ClearRemainingLines();
    }

    public void RenderCollectionList(List<Collection> collections, int selectedIndex, Guid? defaultCollectionId, RenderOptions options)
    {
        Clear();

        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        // Render header box
        WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        var title = TruncateText("Collections", width - 4);
        WriteLine($"\u2551 {title.PadRight(width - 4)} \u2551");

        WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();
        WriteLine();

        // Calculate remaining height for list (reserve 3 lines for status bar area)
        var remainingHeight = Math.Max(3, options.TerminalHeight - _linesWritten - 3);

        if (collections.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            WriteLine("  No collections yet. Use :new <name> to create one.");
            Console.ResetColor();
        }
        else
        {
            // Scroll the viewport to keep the selected collection visible
            var startIndex = 0;
            if (selectedIndex >= remainingHeight)
            {
                startIndex = selectedIndex - remainingHeight + 1;
            }

            var endIndex = Math.Min(collections.Count, startIndex + remainingHeight);
            for (var i = startIndex; i < endIndex; i++)
            {
                var collection = collections[i];
                var isSelected = i == selectedIndex;
                var isDefault = defaultCollectionId.HasValue && collection.Id == defaultCollectionId.Value;

                var prefix = isSelected ? "\u2192" : " ";
                var star = isDefault ? " \u2605" : "";
                var itemCount = collection.Items.Count;
                var countText = $"({itemCount} item{(itemCount == 1 ? "" : "s")})";

                if (isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.White;
                }

                var displayName = TruncateText(collection.Name, options.MaxContentWidth - 20);
                WriteLine($"  {prefix} {displayName} {countText}{star}");
                Console.ResetColor();
            }

            if (collections.Count > endIndex)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                WriteLine($"  ... {collections.Count - endIndex} more collections");
                Console.ResetColor();
            }
        }

        // Status bar
        RenderCollectionStatusBar(ViewMode.CollectionList);

        ClearRemainingLines();
    }

    public void RenderCollectionItems(Collection collection, int selectedIndex, RenderOptions options)
    {
        Clear();

        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        // Render header box with collection name
        WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        var itemCount = collection.Items.Count;
        var headerText = TruncateText($"{collection.Name} ({itemCount} item{(itemCount == 1 ? "" : "s")})", width - 4);
        WriteLine($"\u2551 {headerText.PadRight(width - 4)} \u2551");

        WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();
        WriteLine();

        // Calculate remaining height for list (reserve 3 lines for status bar area)
        var remainingHeight = Math.Max(3, options.TerminalHeight - _linesWritten - 3);

        if (collection.Items.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            WriteLine("  No items in this collection.");
            Console.ResetColor();
        }
        else
        {
            // Each item renders 2 lines (title + domain), so limit by half the remaining height
            var maxItems = Math.Max(1, remainingHeight / 2);

            // Scroll the viewport to keep the selected item visible
            var startIndex = 0;
            if (selectedIndex >= maxItems)
            {
                startIndex = selectedIndex - maxItems + 1;
            }

            var endIndex = Math.Min(collection.Items.Count, startIndex + maxItems);
            for (var i = startIndex; i < endIndex; i++)
            {
                var item = collection.Items[i];
                var isSelected = i == selectedIndex;

                var prefix = isSelected ? "\u2192" : " ";
                var unreadMarker = item.IsRead ? " " : "\u2022";

                // Extract domain from URL
                var domain = "";
                try
                {
                    var uri = new Uri(item.Url);
                    domain = uri.Host;
                }
                catch
                {
                    domain = item.Url;
                }

                if (isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.ForegroundColor = item.IsRead ? ConsoleColor.DarkGray : ConsoleColor.White;
                }

                var displayTitle = TruncateText(item.Title, options.MaxContentWidth - 10);
                WriteLine($"  {prefix}{unreadMarker} {displayTitle}");

                Console.ResetColor();

                if (!isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                }

                var displayDomain = TruncateText(domain, options.MaxContentWidth - 10);
                WriteLine($"      {displayDomain}");
                Console.ResetColor();
            }

            if (collection.Items.Count > endIndex)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                WriteLine($"  ... {collection.Items.Count - endIndex} more items below");
                Console.ResetColor();
            }
        }

        // Status bar
        RenderCollectionStatusBar(ViewMode.CollectionItems);

        ClearRemainingLines();
    }

    public void RenderStatusBar(NavigationContext context, ViewMode mode)
    {
        // Separator line
        WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        WriteLine(new string('\u2500', separatorWidth));

        Console.ForegroundColor = ConsoleColor.Yellow;

        var statusText = mode switch
        {
            ViewMode.Hierarchical => "[LinkView] j/k:move h:collapse l:expand Enter:select v:reader /:search :cmd q:quit",
            ViewMode.Readable => "[ReaderView] j/k:scroll v:links b:back /:search :cmd q:quit",
            _ => "[Browser] q:quit"
        };

        // Show active search
        if (!string.IsNullOrEmpty(context.SearchQuery))
        {
            statusText += $" | /{context.SearchQuery} (n/N)";
        }

        // Add navigation info
        if (context.CanGoBack)
        {
            statusText = $"[\u2190back] {statusText}";
        }

        WriteLine(statusText);
        Console.ResetColor();
    }

    private void RenderCollectionStatusBar(ViewMode mode)
    {
        WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        WriteLine(new string('\u2500', separatorWidth));

        Console.ForegroundColor = ConsoleColor.Yellow;

        var statusText = mode switch
        {
            ViewMode.CollectionList => "[Collections] j/k:move Enter:open s:set-default d:delete :new q:quit",
            ViewMode.CollectionItems => "[Items] j/k:move Enter:open d:remove J/K:reorder b:back :export q:quit",
            _ => "[Collections] q:quit"
        };

        WriteLine(statusText);
        Console.ResetColor();
    }

    public void Clear()
    {
        try
        {
            // Use cursor positioning instead of Console.Clear() to prevent flickering
            Console.SetCursorPosition(0, 0);
            _linesWritten = 0;
        }
        catch
        {
            // SetCursorPosition may fail in some environments, fall back to clear
            try
            {
                Console.Clear();
            }
            catch
            {
                // Clear may also fail, write newlines instead
                for (var i = 0; i < Console.WindowHeight; i++)
                {
                    Console.WriteLine();
                }
            }
        }
    }

    /// <summary>
    /// Clears remaining lines from cursor to bottom of visible area using ANSI escape.
    /// </summary>
    private void ClearRemainingLines()
    {
        try
        {
            var height = Console.WindowHeight;

            while (_linesWritten < height)
            {
                Console.SetCursorPosition(0, _linesWritten);
                Console.Write("\x1b[K");
                _linesWritten++;
            }
        }
        catch
        {
            // Ignore errors in non-standard console environments
        }
    }

    /// <summary>
    /// Writes a line using ANSI escape to clear remainder, and tracks lines written.
    /// </summary>
    private void WriteLine(string text = "")
    {
        try
        {
            if (_linesWritten >= Console.WindowHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);
            Console.Write(text);
            Console.Write("\x1b[K");
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    private void RenderHeader(PageMetadata metadata, string url, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        // Title
        var title = TruncateText(metadata.Title ?? "Untitled", width - 4);
        WriteLine($"\u2551 {title.PadRight(width - 4)} \u2551");

        // URL - use ANSI inline color codes instead of multi-part Write calls
        var displayUrl = TruncateUrl(url, width - 4);
        var urlLine = $"\x1b[90m\u2551 \x1b[37m{displayUrl.PadRight(width - 4)}\x1b[36m \u2551";
        WriteLine(urlLine);

        WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();
        WriteLine();
    }

    private void RenderLinkTree(NavigationTree tree, NavigationContext context, int maxLines, RenderOptions options)
    {
        // Ensure a node is selected before rendering (handles first load and edge cases)
        tree.EnsureSelection();

        var visibleNodes = tree.GetVisibleNodes().ToList();
        var startIndex = context.ScrollOffset;
        var hasMoreNodes = visibleNodes.Count > startIndex + maxLines;

        // Reserve one line for scroll indicator when there are more nodes below
        var maxDisplay = hasMoreNodes ? maxLines - 1 : maxLines;

        // Render visible nodes (group headers are now part of the tree)
        for (var i = startIndex; i < Math.Min(startIndex + maxDisplay, visibleNodes.Count); i++)
        {
            var node = visibleNodes[i];
            RenderLinkNode(node, node.IsSelected, options);
        }

        // Show scroll indicator within the allocated area
        if (hasMoreNodes)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var remaining = Math.Max(0, visibleNodes.Count - startIndex - maxDisplay);
            WriteLine($"  ... {remaining} more links (scroll with j/k)");
            Console.ResetColor();
        }
    }

    private void RenderLinkNode(LinkNode node, bool isSelected, RenderOptions options)
    {
        // Group headers render differently
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

        // Collapse indicator for nodes with children
        var collapseIndicator = node.Children.Count > 0
            ? (node.CollapseState == NodeCollapseState.Expanded ? "\u25bc" : "\u25b6")
            : " ";

        var displayText = TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);
        WriteLine($"{indent}{prefix}{collapseIndicator} {displayText}");

        Console.ResetColor();
    }

    private void RenderGroupHeader(LinkNode node, bool isSelected, RenderOptions options)
    {
        var indent = new string(' ', (node.Depth * 2) + 2);
        var prefix = isSelected ? "\u2192" : " ";

        // Collapse indicator
        var collapseIndicator = node.CollapseState == NodeCollapseState.Expanded ? "\u25bc" : "\u25b6";

        if (isSelected)
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
        }
        else
        {
            // Group headers in white/bold (using white for visibility)
            Console.ForegroundColor = ConsoleColor.White;
        }

        var displayText = TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);

        // Add blank line before group headers (except the first one)
        if (node.Depth == 1)
        {
            WriteLine();
        }

        WriteLine($"{indent}{prefix}{collapseIndicator} {displayText}");

        Console.ResetColor();
    }

    private void RenderArticleHeader(ReadableContent content, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        // Title (may span multiple lines)
        var titleLines = WrapText(content.Title, width - 4);
        foreach (var line in titleLines)
        {
            WriteLine($"\u2551 {line.PadRight(width - 4)} \u2551");
        }

        WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();

        // Metadata line
        Console.ForegroundColor = ConsoleColor.DarkGray;
        WriteLine();
        WriteLine($"  {content.GetMetadataString()}");
        Console.ResetColor();
        WriteLine();
    }

    /// <summary>
    /// Renders pre-wrapped lines with line-based scrolling.
    /// </summary>
    private void RenderLineBasedContent(List<string> allLines, NavigationContext context, int viewportHeight, RenderOptions options)
    {
        var startLine = context.ScrollOffset;
        var hasMoreContent = allLines.Count > viewportHeight && startLine + viewportHeight < allLines.Count;

        // Reserve one line for progress indicator when there's more content below
        var contentLines = hasMoreContent ? viewportHeight - 1 : viewportHeight;
        var endLine = Math.Min(startLine + contentLines, allLines.Count);

        for (var i = startLine; i < endLine; i++)
        {
            if (!string.IsNullOrEmpty(context.SearchQuery))
            {
                WriteLineWithHighlight(allLines[i], context.SearchQuery);
            }
            else
            {
                WriteLine(allLines[i]);
            }
        }

        // Fill remaining content area with empty lines
        var linesRendered = endLine - startLine;
        for (var i = linesRendered; i < contentLines; i++)
        {
            WriteLine();
        }

        // Show progress indicator within the allocated viewport
        if (hasMoreContent)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var progress = allLines.Count > 0
                ? (int)((float)Math.Min(startLine + viewportHeight, allLines.Count) / allLines.Count * 100)
                : 100;
            var remaining = Math.Max(0, allLines.Count - startLine - contentLines);

            var searchInfo = !string.IsNullOrEmpty(context.SearchQuery) ? $" | search: \"{context.SearchQuery}\"" : "";
            WriteLine($"  [{progress}%] {remaining} lines remaining (scroll with j/k){searchInfo}");
            Console.ResetColor();
        }
    }

    private void RenderArticleContent(ReadableContent content, NavigationContext context, int maxLines, RenderOptions options)
    {
        var paragraphs = content.Paragraphs;
        var startParagraph = context.ScrollOffset;
        var maxDisplay = Math.Max(3, maxLines);

        for (var i = startParagraph; i < Math.Min(startParagraph + maxDisplay, paragraphs.Count); i++)
        {
            var wrapped = WrapText(paragraphs[i], options.MaxContentWidth - 4);
            foreach (var line in wrapped)
            {
                if (!string.IsNullOrEmpty(context.SearchQuery))
                {
                    WriteLineWithHighlight($"  {line}", context.SearchQuery);
                }
                else
                {
                    WriteLine($"  {line}");
                }
            }

            WriteLine();
        }

        // Show progress indicator
        if (paragraphs.Count > maxDisplay)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var progress = (int)((float)(startParagraph + maxDisplay) / paragraphs.Count * 100);
            WriteLine();

            var searchInfo = !string.IsNullOrEmpty(context.SearchQuery) ? $" | search: \"{context.SearchQuery}\"" : "";
            WriteLine($"  [{progress}%] {paragraphs.Count - startParagraph - maxDisplay} paragraphs remaining (scroll with j/k){searchInfo}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Writes a line with search query matches highlighted.
    /// </summary>
    private void WriteLineWithHighlight(string text, string searchQuery)
    {
        try
        {
            if (_linesWritten >= Console.WindowHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);

            var index = 0;
            var savedColor = Console.ForegroundColor;

            while (index < text.Length)
            {
                var matchPos = text.IndexOf(searchQuery, index, StringComparison.OrdinalIgnoreCase);
                if (matchPos < 0)
                {
                    Console.Write(text.Substring(index));
                    index = text.Length;
                }
                else
                {
                    // Write text before match
                    if (matchPos > index)
                    {
                        Console.Write(text.Substring(index, matchPos - index));
                    }

                    // Write highlighted match
                    Console.BackgroundColor = ConsoleColor.DarkYellow;
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.Write(text.Substring(matchPos, searchQuery.Length));
                    Console.ResetColor();
                    Console.ForegroundColor = savedColor;

                    index = matchPos + searchQuery.Length;
                }
            }

            // Clear remainder of line with ANSI escape
            Console.Write("\x1b[K");
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (maxLength <= 3)
        {
            return text.Length <= maxLength ? text : text.Substring(0, Math.Max(0, maxLength));
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength - 3) + "...";
    }

    private static string TruncateUrl(string url, int maxLength)
    {
        if (string.IsNullOrEmpty(url) || url.Length <= maxLength)
        {
            return url;
        }

        // Show start and end of URL
        var halfLen = (maxLength - 3) / 2;
        return url.Substring(0, halfLen) + "..." + url.Substring(url.Length - halfLen);
    }

    private static List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = string.Empty;

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxWidth)
            {
                if (!string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(currentLine);
                }

                currentLine = word;
            }
            else
            {
                currentLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
        {
            lines.Add(currentLine);
        }

        return lines;
    }
}
