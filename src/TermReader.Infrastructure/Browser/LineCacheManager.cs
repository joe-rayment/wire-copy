// Educational and personal use only.

using System.Text.RegularExpressions;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser;

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
    private int _cachedWidth;

    public LineCacheManager(NavigationService navigationService, IThemeProvider themeProvider)
    {
        _navigationService = navigationService;
        _themeProvider = themeProvider;
    }

    public IReadOnlyList<string>? CachedLines => _cachedLines;

    public int CachedWidth => _cachedWidth;

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
        var contentLines = WrapAllContent(page.ReadableContent, contentWidth);
        headlineLines.AddRange(contentLines);
        _cachedLines = headlineLines;
        _cachedWidth = contentWidth;
    }

    /// <summary>
    /// Invalidates the line cache so it is rebuilt on next access.
    /// </summary>
    public void InvalidateLineCache()
    {
        _cachedLines = null;
        _cachedWidth = 0;
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

        // Count character offset up to the current scroll line (strip ANSI codes for accurate length)
        var charOffset = 0;
        for (var i = 0; i < Math.Min(currentScroll, _cachedLines.Count); i++)
        {
            charOffset += VisibleLength(_cachedLines[i].TrimStart()) + 1; // +1 for implicit newline
        }

        // Invalidate and rebuild with new width
        InvalidateLineCache();
        EnsureLineCache(newOptions);

        if (_cachedLines == null || _cachedLines.Count == 0)
        {
            return;
        }

        // Find the line index in new lines matching the character offset
        var accumulatedChars = 0;
        var newLineIndex = 0;
        for (var i = 0; i < _cachedLines.Count; i++)
        {
            accumulatedChars += VisibleLength(_cachedLines[i].TrimStart()) + 1;
            if (accumulatedChars >= charOffset)
            {
                newLineIndex = i;
                break;
            }

            newLineIndex = i;
        }

        _navigationService.SetScrollOffset(Math.Clamp(newLineIndex, 0, Math.Max(0, _cachedLines.Count - 1)));
    }

    /// <summary>
    /// Builds styled headline lines (bold/uppercase title + secondary-text metadata)
    /// matching the launcher tile label style. These lines are prepended to the line cache
    /// so the headline scrolls with the content.
    /// </summary>
    internal static List<string> BuildHeadlineLines(ReadableContent content, int maxWidth, ThemePalette palette, string? pageUrl = null)
    {
        const string bold = "\x1b[1m";
        const string dim = "\x1b[2m";
        const string reset = "\x1b[0m";
        var titleColor = palette.HeaderTitleFg.AnsiFg;

        var lines = new List<string>();
        lines.Add(string.Empty);

        var titleLines = UI.Renderers.RenderHelpers.WrapText(content.Title.ToUpperInvariant(), maxWidth - 4);
        foreach (var line in titleLines)
        {
            lines.Add($"  {bold}{titleColor}{line}{reset}");
        }

        var metadata = content.GetMetadataString();
        if (!string.IsNullOrEmpty(metadata))
        {
            lines.Add($"  {palette.SecondaryText.AnsiFg}{dim}{metadata}{reset}");
        }

        if (!string.IsNullOrEmpty(pageUrl))
        {
            var domain = UI.Renderers.LauncherRenderer.ExtractDomain(pageUrl);
            lines.Add($"  {palette.SecondaryText.AnsiFg}{dim}{domain}{reset}");
        }

        lines.Add(string.Empty);
        return lines;
    }

    /// <summary>
    /// Pre-wraps all paragraphs into a flat list of display lines for the reader view.
    /// </summary>
    internal static List<string> WrapAllContent(ReadableContent content, int maxWidth)
    {
        var allLines = new List<string>();
        foreach (var paragraph in content.Paragraphs)
        {
            var wrapped = UI.Renderers.RenderHelpers.WrapText(paragraph, maxWidth - 4);
            foreach (var line in wrapped)
            {
                allLines.Add($"  {line}");
            }

            allLines.Add(string.Empty);
        }

        return allLines;
    }

    /// <summary>
    /// Returns the visible length of a string after stripping ANSI escape sequences.
    /// </summary>
    internal static int VisibleLength(string text)
    {
        return AnsiEscapeRegex.Replace(text, string.Empty).Length;
    }

    /// <summary>
    /// Pre-populates the cache for testing purposes.
    /// </summary>
    internal void SetCacheForTesting(List<string> lines, int width)
    {
        _cachedLines = lines;
        _cachedWidth = width;
    }
}
