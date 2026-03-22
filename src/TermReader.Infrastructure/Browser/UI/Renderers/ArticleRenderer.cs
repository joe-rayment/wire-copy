// Educational and personal use only.

using System.Text;
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
        var atEndOfArticle = endLine >= allLines.Count;

        if (atEndOfArticle && linesRendered + 5 <= viewportHeight)
        {
            var width = Math.Max(1, options.TerminalWidth - 2);
            var wordCount = EstimateWordCount(allLines);
            var readTimeMinutes = Math.Max(1, (int)Math.Ceiling(wordCount / 250.0));
            RenderEndOfArticleFooter(p, width, wordCount, readTimeMinutes);
            linesRendered += 5;
        }

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
        else
        {
            var width = Math.Max(1, options.TerminalWidth - 2);
            var wordCount = 0;
            foreach (var para in paragraphs)
            {
                wordCount += string.IsNullOrWhiteSpace(para)
                    ? 0
                    : para.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            }

            var readTimeMinutes = Math.Max(1, (int)Math.Ceiling(wordCount / 250.0));
            RenderEndOfArticleFooter(p, width, wordCount, readTimeMinutes);
        }
    }

    private static int EstimateWordCount(List<string> lines)
    {
        var count = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Strip ANSI escape sequences before counting words
            var clean = StripAnsi(line).Trim();
            if (clean.Length > 0)
            {
                count += clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            }
        }

        return count;
    }

    private static string StripAnsi(string input)
    {
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '\x1b' && i + 1 < input.Length && input[i + 1] == '[')
            {
                i += 2;
                while (i < input.Length && input[i] != 'm')
                {
                    i++;
                }

                if (i < input.Length)
                {
                    i++; // skip 'm'
                }
            }
            else
            {
                sb.Append(input[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private void RenderEndOfArticleFooter(ThemePalette p, int width, int wordCount, int readTimeMinutes)
    {
        var mutedFg = p.GetMutedFg().AnsiFg;
        var accentFg = p.GetAccentFg().AnsiFg;
        var secondaryFg = p.SecondaryText.AnsiFg;

        // Line 1: blank
        _helpers.WriteLine();

        // Line 2: "— end —" centered
        var endMarker = "\u2014 end \u2014";
        var endPad = Math.Max(0, (width - endMarker.Length) / 2);
        _helpers.WriteLine($"{new string(' ', endPad)}{secondaryFg}{endMarker}{Reset}");

        // Line 3: stats line centered
        var wordCountFormatted = wordCount.ToString("N0");
        var statsText = $"{readTimeMinutes} min read \u00b7 {wordCountFormatted} words";
        var statsPad = Math.Max(0, (width - statsText.Length) / 2);
        _helpers.WriteLine($"{new string(' ', statsPad)}{mutedFg}{statsText}{Reset}");

        // Line 4: action hints centered
        var hintsPlain = "s:save  b:back";
        var hintsPad = Math.Max(0, (width - hintsPlain.Length) / 2);
        _helpers.WriteLine(
            $"{new string(' ', hintsPad)}{accentFg}s{Reset}{secondaryFg}:save{Reset}  {accentFg}b{Reset}{secondaryFg}:back{Reset}");

        // Line 5: blank
        _helpers.WriteLine();
    }
}
