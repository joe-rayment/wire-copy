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

    public void RenderLineBasedContent(List<string> allLines, NavigationContext context, int viewportHeight, RenderOptions options, int focusLineOffset = -1)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var startLine = context.ScrollOffset;
        var endLine = Math.Min(startLine + viewportHeight, allLines.Count);
        var indicatorAnsi = $"{p.FocusIndicatorFg.AnsiFg}\u258e{Reset}";

        for (var i = startLine; i < endLine; i++)
        {
            var viewportLine = i - startLine;

            if (focusLineOffset >= 0 && viewportLine == focusLineOffset)
            {
                _helpers.WriteLineWithIndicator(allLines[i], indicatorAnsi, context.SearchQuery, !string.IsNullOrEmpty(context.SearchQuery) ? p : null);
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

}
