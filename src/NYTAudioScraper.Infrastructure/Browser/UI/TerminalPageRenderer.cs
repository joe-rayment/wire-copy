// Educational and personal use only.

using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.DTOs.Browser;
using NYTAudioScraper.Application.Interfaces.Browser;
using NYTAudioScraper.Domain.Entities.Browser;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;

namespace NYTAudioScraper.Infrastructure.Browser.UI;

/// <summary>
/// Renders pages to the terminal.
/// Handles both hierarchical (link tree) and readable (article) views.
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

        // Render link tree
        if (page.LinkTree != null)
        {
            RenderLinkTree(page.LinkTree, context, options);
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

    public void RenderReadable(Page page, NavigationContext context, RenderOptions options)
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

        // Render article content
        RenderArticleContent(page.ReadableContent, context, options);

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

    public void RenderStatusBar(NavigationContext context, ViewMode mode)
    {
        // Move to bottom of screen
        WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Write("─".PadRight(Console.WindowWidth - 1, '─'));
        WriteLine();

        Console.ForegroundColor = ConsoleColor.Yellow;

        var statusText = mode switch
        {
            ViewMode.Hierarchical => "[LinkView] j/k:move h:collapse l:expand Enter:select v:reader q:quit",
            ViewMode.Readable => "[ReaderView] j/k:scroll v:links b:back q:quit",
            _ => "[Browser] q:quit"
        };

        // Add navigation info
        if (context.CanGoBack)
        {
            statusText = $"[←back] {statusText}";
        }

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
    /// Clears remaining lines from cursor to bottom of visible area.
    /// Call this after rendering to clear old content.
    /// </summary>
    private void ClearRemainingLines()
    {
        try
        {
            var height = Console.WindowHeight;
            var width = Console.WindowWidth;
            var emptyLine = new string(' ', width);

            while (_linesWritten < height)
            {
                Console.Write(emptyLine);
                _linesWritten++;
            }
        }
        catch
        {
            // Ignore errors in non-standard console environments
        }
    }

    /// <summary>
    /// Writes a line and tracks lines written for clearing.
    /// </summary>
    private void WriteLine(string text = "")
    {
        try
        {
            // Pad the line to clear any previous content on this line
            var width = Console.WindowWidth;
            if (text.Length < width)
            {
                Console.Write(text);
                Console.WriteLine(new string(' ', width - text.Length - 1));
            }
            else
            {
                Console.WriteLine(text);
            }

            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    /// <summary>
    /// Writes text without newline and pads to clear previous content.
    /// </summary>
    private void Write(string text)
    {
        Console.Write(text);
    }

    private void RenderHeader(PageMetadata metadata, string url, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        WriteLine($"╔{'═'.ToString().PadRight(width - 2, '═')}╗");

        // Title
        var title = TruncateText(metadata.Title ?? "Untitled", width - 4);
        WriteLine($"║ {title.PadRight(width - 4)} ║");

        // URL
        var displayUrl = TruncateUrl(url, width - 4);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Write("║ ");
        Console.ForegroundColor = ConsoleColor.Gray;
        Write(displayUrl.PadRight(width - 4));
        Console.ForegroundColor = ConsoleColor.Cyan;
        WriteLine(" ║");

        WriteLine($"╚{'═'.ToString().PadRight(width - 2, '═')}╝");
        Console.ResetColor();
        WriteLine();
    }

    private void RenderLinkTree(NavigationTree tree, NavigationContext context, RenderOptions options)
    {
        // Ensure a node is selected before rendering (handles first load and edge cases)
        tree.EnsureSelection();

        var visibleNodes = tree.GetVisibleNodes().ToList();
        var startIndex = context.ScrollOffset;
        var maxDisplay = options.ContentHeight;

        // Render visible nodes (group headers are now part of the tree)
        for (var i = startIndex; i < Math.Min(startIndex + maxDisplay, visibleNodes.Count); i++)
        {
            var node = visibleNodes[i];
            RenderLinkNode(node, node.IsSelected, options);
        }

        // Show scroll indicator if needed
        if (visibleNodes.Count > maxDisplay)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            WriteLine();
            WriteLine($"  ... {visibleNodes.Count - maxDisplay - startIndex} more links (scroll with j/k)");
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
        var prefix = isSelected ? "→" : " ";

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
            ? (node.CollapseState == NodeCollapseState.Expanded ? "▼" : "▶")
            : " ";

        var displayText = TruncateText(node.Link.DisplayText, options.MaxContentWidth - indent.Length - 5);
        WriteLine($"{indent}{prefix}{collapseIndicator} {displayText}");

        Console.ResetColor();
    }

    private void RenderGroupHeader(LinkNode node, bool isSelected, RenderOptions options)
    {
        var indent = new string(' ', (node.Depth * 2) + 2);
        var prefix = isSelected ? "→" : " ";

        // Collapse indicator
        var collapseIndicator = node.CollapseState == NodeCollapseState.Expanded ? "▼" : "▶";

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
        WriteLine($"╔{'═'.ToString().PadRight(width - 2, '═')}╗");

        // Title (may span multiple lines)
        var titleLines = WrapText(content.Title, width - 4);
        foreach (var line in titleLines)
        {
            WriteLine($"║ {line.PadRight(width - 4)} ║");
        }

        WriteLine($"╚{'═'.ToString().PadRight(width - 2, '═')}╝");
        Console.ResetColor();

        // Metadata line
        Console.ForegroundColor = ConsoleColor.DarkGray;
        WriteLine();
        WriteLine($"  {content.GetMetadataString()}");
        Console.ResetColor();
        WriteLine();
    }

    private void RenderArticleContent(ReadableContent content, NavigationContext context, RenderOptions options)
    {
        var paragraphs = content.Paragraphs;
        var startParagraph = context.ScrollOffset;
        var maxDisplay = Math.Max(3, options.ContentHeight - 6);

        for (var i = startParagraph; i < Math.Min(startParagraph + maxDisplay, paragraphs.Count); i++)
        {
            var wrapped = WrapText(paragraphs[i], options.MaxContentWidth - 4);
            foreach (var line in wrapped)
            {
                WriteLine($"  {line}");
            }

            WriteLine();
        }

        // Show progress indicator
        if (paragraphs.Count > maxDisplay)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var progress = (int)((float)(startParagraph + maxDisplay) / paragraphs.Count * 100);
            WriteLine();
            WriteLine($"  [{progress}%] {paragraphs.Count - startParagraph - maxDisplay} paragraphs remaining (scroll with j/k)");
            Console.ResetColor();
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
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
