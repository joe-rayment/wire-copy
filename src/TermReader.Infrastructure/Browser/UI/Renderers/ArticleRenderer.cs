// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders article/reader view content including headers, line-based content, and status bar.
/// </summary>
internal class ArticleRenderer
{
    private readonly RenderHelpers _helpers;

    public ArticleRenderer(RenderHelpers helpers)
    {
        _helpers = helpers;
    }

    public void RenderArticleHeader(ReadableContent content, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        _helpers.WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        var titleLines = RenderHelpers.WrapText(content.Title, width - 4);
        foreach (var line in titleLines)
        {
            _helpers.WriteLine($"\u2551 {line.PadRight(width - 4)} \u2551");
        }

        _helpers.WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        _helpers.WriteLine();
        _helpers.WriteLine($"  {content.GetMetadataString()}");
        Console.ResetColor();
        _helpers.WriteLine();
    }

    public void RenderLineBasedContent(List<string> allLines, NavigationContext context, int viewportHeight, RenderOptions options)
    {
        var startLine = context.ScrollOffset;
        var endLine = Math.Min(startLine + viewportHeight, allLines.Count);

        for (var i = startLine; i < endLine; i++)
        {
            if (i == startLine)
            {
                _helpers.WriteLineWithFocusHighlight(allLines[i], options.Use256Colors);
            }
            else if (!string.IsNullOrEmpty(context.SearchQuery))
            {
                _helpers.WriteLineWithHighlight(allLines[i], context.SearchQuery);
            }
            else
            {
                _helpers.WriteLine(allLines[i]);
            }
        }

        var linesRendered = endLine - startLine;
        for (var i = linesRendered; i < viewportHeight; i++)
        {
            _helpers.WriteLine();
        }
    }

    public void RenderArticleContent(ReadableContent content, NavigationContext context, int maxLines, RenderOptions options)
    {
        var paragraphs = content.Paragraphs;
        var startParagraph = context.ScrollOffset;
        var maxDisplay = Math.Max(3, maxLines);

        for (var i = startParagraph; i < Math.Min(startParagraph + maxDisplay, paragraphs.Count); i++)
        {
            var wrapped = RenderHelpers.WrapText(paragraphs[i], options.MaxContentWidth - 4);
            foreach (var line in wrapped)
            {
                if (!string.IsNullOrEmpty(context.SearchQuery))
                {
                    _helpers.WriteLineWithHighlight($"  {line}", context.SearchQuery);
                }
                else
                {
                    _helpers.WriteLine($"  {line}");
                }
            }

            _helpers.WriteLine();
        }

        if (paragraphs.Count > maxDisplay)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var progress = (int)((float)(startParagraph + maxDisplay) / paragraphs.Count * 100);
            _helpers.WriteLine();

            var searchInfo = !string.IsNullOrEmpty(context.SearchQuery) ? $" | search: \"{context.SearchQuery}\"" : string.Empty;
            _helpers.WriteLine($"  [{progress}%] {paragraphs.Count - startParagraph - maxDisplay} paragraphs remaining (scroll with j/k){searchInfo}");
            Console.ResetColor();
        }
    }

    public void RenderReaderStatusBar(NavigationContext context, int totalLines, int contentWidth)
    {
        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        _helpers.WriteLine(new string('\u2500', separatorWidth));

        var progress = totalLines > 0
            ? (int)((float)Math.Min(context.ScrollOffset + 20, totalLines) / totalLines * 100)
            : 100;

        var lineInfo = $"L{context.ScrollOffset + 1}/{totalLines}";
        var widthInfo = $"W{contentWidth}";
        var progressInfo = $"{progress}%";

        var searchInfo = !string.IsNullOrEmpty(context.SearchQuery)
            ? $" /{context.SearchQuery}"
            : string.Empty;

        var hints = "j/k:scroll v:links b:back /:search :cmd q:quit";

        Console.ForegroundColor = ConsoleColor.Yellow;
        _helpers.WriteLine($"[Reader] {lineInfo} {widthInfo} {progressInfo}{searchInfo} | {hints}");
        Console.ResetColor();
    }
}
