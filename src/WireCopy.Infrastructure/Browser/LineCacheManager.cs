// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Manages the pre-wrapped line cache for the reader view.
/// Handles line wrapping, cache invalidation, and scroll position
/// preservation when content width changes.
/// </summary>
internal class LineCacheManager
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    private readonly NavigationService _navigationService;
    private readonly IThemeProvider _themeProvider;

    private List<string>? _cachedLines;
    private List<ParagraphSpan>? _paragraphSpans;
    private int _cachedWidth;
    private int _contentLineCount;

    public LineCacheManager(NavigationService navigationService, IThemeProvider themeProvider)
    {
        _navigationService = navigationService;
        _themeProvider = themeProvider;
    }

    public IReadOnlyList<string>? CachedLines => _cachedLines;

    public IReadOnlyList<ParagraphSpan>? ParagraphSpans => _paragraphSpans;

    public int CachedWidth => _cachedWidth;

    /// <summary>
    /// workspace-1m3h.4: number of cached lines that are real article content
    /// (headline + paragraphs), i.e. the index where the appended end-of-article
    /// footer starts. Speed reading stops here instead of narrating the footer.
    /// Zero when the cache is empty.
    /// </summary>
    public int ContentLineCount => _contentLineCount;

    /// <summary>
    /// Ensures the line cache is populated and matches the current content width.
    /// Rebuilds if width changed or cache is empty.
    /// </summary>
    public void EnsureLineCache(RenderOptions options)
    {
        var contentWidth = options.MaxContentWidth;
        if (_cachedLines != null && _cachedWidth == contentWidth)
        {
            return;
        }

        var page = _navigationService.CurrentPage;
        if (page?.ReadableContent == null)
        {
            _cachedLines = null;
            return;
        }

        var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var headlineLines = BuildHeadlineLines(page.ReadableContent, contentWidth, palette, page.Url);
        var (contentLines, spans) = WrapAllContentWithSpans(page.ReadableContent, contentWidth);

        // Offset paragraph spans by headline line count so they reference absolute indices
        var headlineCount = headlineLines.Count;
        _paragraphSpans = new List<ParagraphSpan>(spans.Count);
        foreach (var span in spans)
        {
            _paragraphSpans.Add(new ParagraphSpan(span.StartLine + headlineCount, span.EndLine + headlineCount));
        }

        headlineLines.AddRange(contentLines);

        // workspace-1m3h.4: append the end-of-article footer (— end — / stats)
        // to the cache itself so it scrolls into view as real content on long
        // articles, instead of only being painted when spare viewport rows
        // happened to remain (which only short articles ever had).
        _contentLineCount = headlineLines.Count;
        headlineLines.AddRange(BuildEndOfArticleFooterLines(page.ReadableContent, contentWidth, palette));

        _cachedLines = headlineLines;
        _cachedWidth = contentWidth;
    }

    /// <summary>
    /// Invalidates the line cache so it is rebuilt on next access.
    /// </summary>
    public void InvalidateLineCache()
    {
        _cachedLines = null;
        _paragraphSpans = null;
        _cachedWidth = 0;
        _contentLineCount = 0;
    }

    /// <summary>
    /// Clamps the scroll offset to valid bounds for the current cache.
    /// </summary>
    public void ClampScrollOffset()
    {
        var context = _navigationService.CurrentContext;
        if (context.ViewMode == Domain.Enums.Browser.ViewMode.Readable && _cachedLines != null && _cachedLines.Count > 0)
        {
            var maxScroll = Math.Max(0, _cachedLines.Count - 1);
            if (context.ScrollOffset > maxScroll)
            {
                _navigationService.SetScrollOffset(maxScroll);
            }
        }
    }

    /// <summary>
    /// Returns the ParagraphSpan containing the given line index, or null if the line
    /// is a headline, blank separator, or otherwise not within a paragraph.
    /// </summary>
    public ParagraphSpan? GetParagraphForLine(int lineIndex)
    {
        if (_paragraphSpans == null)
        {
            return null;
        }

        foreach (var span in _paragraphSpans)
        {
            if (lineIndex >= span.StartLine && lineIndex <= span.EndLine)
            {
                return span;
            }

            if (span.StartLine > lineIndex)
            {
                break; // Spans are sorted — no point searching further
            }
        }

        return null;
    }

    /// <summary>
    /// Preserves the reading position when content width changes by computing
    /// the character offset of the current scroll position in the old lines,
    /// re-wrapping with the new width, and finding the matching line index.
    /// </summary>
    public void PreserveScrollPositionAfterRewrap(RenderOptions newOptions)
    {
        var page = _navigationService.CurrentPage;
        if (page?.ReadableContent == null || _cachedLines == null || _cachedLines.Count == 0)
        {
            InvalidateLineCache();
            return;
        }

        var currentScroll = _navigationService.CurrentContext.ScrollOffset;
        var currentCursor = _navigationService.ReaderCursorLine;

        // Count character offset up to the current scroll and cursor lines
        var scrollCharOffset = ComputeCharOffset(currentScroll);
        var cursorCharOffset = ComputeCharOffset(currentCursor);

        // Invalidate and rebuild with new width
        InvalidateLineCache();
        EnsureLineCache(newOptions);

        if (_cachedLines == null || _cachedLines.Count == 0)
        {
            return;
        }

        // Find the line indices in new lines matching the character offsets
        var newScrollIndex = FindLineByCharOffset(scrollCharOffset);
        var newCursorIndex = FindLineByCharOffset(cursorCharOffset);
        var maxLine = Math.Max(0, _cachedLines.Count - 1);

        _navigationService.SetScrollOffset(Math.Clamp(newScrollIndex, 0, maxLine));
        _navigationService.SetReaderCursorLine(Math.Clamp(newCursorIndex, 0, maxLine));
    }

    /// <summary>
    /// Builds styled headline lines (bold/uppercase title + secondary-text metadata)
    /// matching the launcher tile label style. These lines are prepended to the line cache
    /// so the headline scrolls with the content.
    /// </summary>
    internal static List<string> BuildHeadlineLines(ReadableContent content, int maxWidth, ThemePalette palette, string? pageUrl = null)
    {
        const string bold = "\x1b[1m";
        const string reset = "\x1b[0m";
        var titleColor = palette.HeaderTitleFg.AnsiFg;

        var lines = new List<string>();
        lines.Add(string.Empty);

        // Article headline — bold pink, Title Case AS PUBLISHED (design casing
        // table, workspace-7t0a.6 — no forced uppercase).
        var titleLines = UI.Renderers.RenderHelpers.WrapText(content.Title, maxWidth - 4);
        foreach (var line in titleLines)
        {
            lines.Add($"  {bold}{titleColor}{line}{reset}");
        }

        // Separator under headline for visual weight
        var ruleWidth = Math.Min(maxWidth - 4, 40);
        lines.Add($"  {palette.HeaderBorderFg.AnsiFg}{new string('\u2500', ruleWidth)}{reset}");

        // Byline: 'Author · Date · domain.com' on ONE secondary-text line
        // (workspace-7t0a.6 — domain folds into the byline, no separate row).
        var bylineParts = new List<string>(2);
        var metadata = content.GetMetadataString();
        if (!string.IsNullOrEmpty(metadata))
        {
            bylineParts.Add(metadata);
        }

        if (!string.IsNullOrEmpty(pageUrl))
        {
            var domain = UI.Renderers.LauncherRenderer.ExtractDomain(pageUrl);
            if (!string.IsNullOrEmpty(domain))
            {
                bylineParts.Add(domain);
            }
        }

        if (bylineParts.Count > 0)
        {
            lines.Add($"  {palette.SecondaryText.AnsiFg}{string.Join(" \u00b7 ", bylineParts)}{reset}");
        }

        lines.Add(string.Empty);
        lines.Add(string.Empty);
        return lines;
    }

    /// <summary>
    /// workspace-1m3h.4: builds the end-of-article footer lines (blank, centered
    /// "— end —", centered "N min read · X words", blank) that are appended to
    /// the line cache so they scroll into view like any other content. Centered
    /// within the content column; the renderer's LeftMargin centers the column
    /// itself in the terminal.
    /// </summary>
    internal static List<string> BuildEndOfArticleFooterLines(ReadableContent content, int maxWidth, ThemePalette palette)
    {
        const string reset = "\x1b[0m";

        var wordCount = 0;
        foreach (var para in content.Paragraphs)
        {
            wordCount += string.IsNullOrWhiteSpace(para)
                ? 0
                : para.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        var readTimeMinutes = Math.Max(1, (int)Math.Ceiling(wordCount / 250.0));

        // Design footer spec (workspace-7t0a.6): words first, approximate read-time.
        var endMarker = "— end —";
        var statsText = $"{wordCount:N0} words · ~{readTimeMinutes} min read";

        return new List<string>(4)
        {
            string.Empty,
            CenterLine(endMarker, maxWidth, palette.SecondaryText.AnsiFg, reset),
            CenterLine(statsText, maxWidth, palette.GetMutedFg().AnsiFg, reset),
            string.Empty,
        };
    }

    /// <summary>
    /// Pre-wraps all paragraphs into a flat list of display lines for the reader view.
    /// </summary>
    internal static List<string> WrapAllContent(ReadableContent content, int maxWidth)
    {
        return WrapAllContentWithSpans(content, maxWidth).Lines;
    }

    /// <summary>
    /// Pre-wraps all paragraphs and tracks paragraph boundaries as ParagraphSpans.
    /// Span indices are relative to the returned lines (not offset by headline).
    /// </summary>
    internal static (List<string> Lines, List<ParagraphSpan> Spans) WrapAllContentWithSpans(ReadableContent content, int maxWidth)
    {
        var allLines = new List<string>();
        var spans = new List<ParagraphSpan>();

        foreach (var paragraph in content.Paragraphs)
        {
            var wrapped = UI.Renderers.RenderHelpers.WrapText(paragraph, maxWidth - 4);
            if (wrapped.Count == 0)
            {
                allLines.Add(string.Empty);
                continue;
            }

            var startLine = allLines.Count;
            foreach (var line in wrapped)
            {
                allLines.Add($"  {line}");
            }

            spans.Add(new ParagraphSpan(startLine, allLines.Count - 1));
            allLines.Add(string.Empty); // Paragraph separator
        }

        return (allLines, spans);
    }

    /// <summary>
    /// Returns the visible length of a string after stripping ANSI escape sequences.
    /// </summary>
    internal static int VisibleLength(string text)
    {
        return AnsiEscapeRegex.Replace(text, string.Empty).Length;
    }

    /// <summary>
    /// Pre-populates the cache for testing purposes. All lines are treated as
    /// content unless <paramref name="contentLineCount"/> marks a footer start.
    /// </summary>
    internal void SetCacheForTesting(List<string> lines, int width, int contentLineCount = -1)
    {
        _cachedLines = lines;
        _cachedWidth = width;
        _contentLineCount = contentLineCount >= 0 ? contentLineCount : lines.Count;
    }

    private static string CenterLine(string text, int maxWidth, string colorAnsi, string reset)
    {
        var pad = Math.Max(0, (maxWidth - text.Length) / 2);
        return $"{new string(' ', pad)}{colorAnsi}{text}{reset}";
    }

    private int ComputeCharOffset(int lineIndex)
    {
        var offset = 0;
        for (var i = 0; i < Math.Min(lineIndex, _cachedLines!.Count); i++)
        {
            offset += VisibleLength(_cachedLines[i].TrimStart()) + 1;
        }

        return offset;
    }

    private int FindLineByCharOffset(int charOffset)
    {
        var accumulated = 0;
        var lineIndex = 0;
        for (var i = 0; i < _cachedLines!.Count; i++)
        {
            accumulated += VisibleLength(_cachedLines[i].TrimStart()) + 1;
            if (accumulated >= charOffset)
            {
                lineIndex = i;
                break;
            }

            lineIndex = i;
        }

        return lineIndex;
    }

    /// <summary>
    /// A paragraph's range within the flat line cache (StartLine and EndLine are inclusive).
    /// </summary>
    internal record struct ParagraphSpan(int StartLine, int EndLine);
}
