// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders article/reader view content including headers, line-based content, and status bar.
/// </summary>
internal class ArticleRenderer
{
    private const string Reset = "\x1b[0m";
    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public ArticleRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    public void RenderArticleHeader(ReadableContent content, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var border = p.HeaderBorderFg.AnsiFg;
        var titleFg = p.HeaderTitleFg.AnsiFg;

        _helpers.WriteLine();
        _helpers.WriteLine($"{border}\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557{Reset}");

        var titleLines = RenderHelpers.WrapText(content.Title, width - 4);
        foreach (var line in titleLines)
        {
            _helpers.WriteLine($"{border}\u2551 {titleFg}{line.PadRight(width - 4)}{border} \u2551{Reset}");
        }

        _helpers.WriteLine($"{border}\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d{Reset}");

        _helpers.WriteLine();
        _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{content.GetMetadataString()}{Reset}");
        _helpers.WriteLine();
    }

    public void RenderLineBasedContent(List<string> allLines, NavigationContext context, int viewportHeight, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var startLine = context.ScrollOffset;
        var endLine = Math.Min(startLine + viewportHeight, allLines.Count);

        for (var i = startLine; i < endLine; i++)
        {
            if (i == startLine)
            {
                _helpers.WriteLineWithFocusHighlight(allLines[i], p);
            }
            else if (!string.IsNullOrEmpty(context.SearchQuery))
            {
                _helpers.WriteLineWithHighlight(allLines[i], context.SearchQuery, p);
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
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
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
                    _helpers.WriteLineWithHighlight($"  {line}", context.SearchQuery, p);
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
            var progress = (int)((float)(startParagraph + maxDisplay) / paragraphs.Count * 100);
            _helpers.WriteLine();

            var searchInfo = !string.IsNullOrEmpty(context.SearchQuery) ? $" | search: \"{context.SearchQuery}\"" : string.Empty;
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}[{progress}%] {paragraphs.Count - startParagraph - maxDisplay} paragraphs remaining (scroll with j/k){searchInfo}{Reset}");
        }
    }

    public void RenderReaderStatusBar(NavigationContext context, int totalLines, int contentWidth, int viewportHeight)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        _helpers.WriteLine();
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        _helpers.WriteLine($"{p.StatusBarSeparatorFg.AnsiFg}{new string('\u2500', separatorWidth)}{Reset}");

        var progress = totalLines > 0
            ? (int)((float)Math.Min(context.ScrollOffset + viewportHeight, totalLines) / totalLines * 100)
            : 100;

        var lineInfo = $"L{context.ScrollOffset + 1}/{totalLines}";
        var widthInfo = $"W{contentWidth}";
        var progressInfo = $"{progress}%";
        var themeName = _themeProvider.CurrentTheme.ToString();

        var searchInfo = !string.IsNullOrEmpty(context.SearchQuery)
            ? $" /{context.SearchQuery}"
            : string.Empty;

        var hints = "j/k:scroll v:links b:back /:search :cmd q:quit";

        _helpers.WriteLine($"{p.StatusBarTextFg.AnsiFg}[Reader | {themeName}] {lineInfo} {widthInfo} {progressInfo}{searchInfo} | {hints}{Reset}");
    }
}
