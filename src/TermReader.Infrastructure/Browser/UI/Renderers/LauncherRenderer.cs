// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the launcher home screen with a full-viewport grid layout.
/// Items expand to fill available space with thin separators between cells.
/// </summary>
internal class LauncherRenderer
{
    private readonly RenderHelpers _helpers;

    public LauncherRenderer(RenderHelpers helpers)
    {
        _helpers = helpers;
    }

    public void RenderLauncher(List<Bookmark> bookmarks, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        // Header: TermReader (Cyan) left + bookmark count + version right (1 line + separator)
        var title = $"{Colors.Fg256Cyan}TermReader{Colors.Reset}";
        var statusInfo = $"{Colors.Fg256DarkGray}{bookmarks.Count} bookmarks{Colors.Reset}";
        var version = $"{Colors.Fg256DarkGray}v1.0{Colors.Reset}";
        var headerRight = $"{statusInfo} {version}";

        // Calculate padding (accounting for ANSI escape codes in length)
        var titleTextLen = "TermReader".Length;
        var rightTextLen = $"{bookmarks.Count} bookmarks v1.0".Length;
        var padding = Math.Max(1, width - titleTextLen - rightTextLen - 2);
        _helpers.WriteLine($" {title}{new string(' ', padding)}{headerRight} ");

        // Header separator
        _helpers.WriteLine($"{Colors.Fg256DarkGray}{new string('\u2500', width)}{Colors.Reset}");

        // Calculate grid dimensions
        var headerLines = _helpers.LinesWritten;
        var footerLines = 2; // separator + key hints
        var availableHeight = Math.Max(4, options.TerminalHeight - headerLines - footerLines);

        var columns = width >= 35 ? 2 : 1;
        var totalItems = bookmarks.Count + 1; // +1 for Collections tile

        if (bookmarks.Count == 0 && totalItems == 1)
        {
            _helpers.WriteLine();
            _helpers.WriteLine($"  {Colors.Fg256DarkGray}No bookmarks. Press 'a' to add one.{Colors.Reset}");
            _helpers.WriteLine();
            _helpers.WriteLine($"  {Colors.Fg256Cyan}\u2502{Colors.Reset} {Colors.Fg256White}Collections{Colors.Reset}");
            return;
        }

        var totalRows = (totalItems + columns - 1) / columns;
        var visibleRows = Math.Max(1, availableHeight / Math.Max(3, availableHeight / Math.Max(1, totalRows)));
        var rowHeight = Math.Max(3, availableHeight / Math.Max(1, Math.Min(visibleRows, totalRows)));

        // Recalculate visible rows with actual row height
        visibleRows = Math.Max(1, availableHeight / rowHeight);
        var startRow = scrollOffset;

        var cellWidth = columns == 1 ? width : (width - 1) / 2; // -1 for vertical separator

        for (var row = startRow; row < Math.Min(startRow + visibleRows, totalRows); row++)
        {
            // Horizontal separator between rows (not before first)
            if (row > startRow)
            {
                if (columns == 2)
                {
                    var halfSep = cellWidth;
                    _helpers.WriteLine($"{Colors.Fg256DarkGray}{new string('\u2500', halfSep)}\u253c{new string('\u2500', width - halfSep - 1)}{Colors.Reset}");
                }
                else
                {
                    _helpers.WriteLine($"{Colors.Fg256DarkGray}{new string('\u2500', width)}{Colors.Reset}");
                }
            }

            var leftIdx = row * columns;
            var rightIdx = columns == 2 ? leftIdx + 1 : -1;

            var contentLines = rowHeight;

            for (var line = 0; line < contentLines; line++)
            {
                var sb = new System.Text.StringBuilder();

                // Left cell
                sb.Append(BuildCellContent(bookmarks, leftIdx, selectedIndex, cellWidth, contentLines, line));

                // Vertical separator + right cell
                if (columns == 2)
                {
                    sb.Append($"{Colors.Fg256DarkGray}\u2502{Colors.Reset}");

                    if (rightIdx >= 0 && rightIdx < totalItems)
                    {
                        sb.Append(BuildCellContent(bookmarks, rightIdx, selectedIndex, width - cellWidth - 1, contentLines, line));
                    }
                    else
                    {
                        sb.Append(new string(' ', width - cellWidth - 1));
                    }
                }

                _helpers.WriteLine(sb.ToString());
            }
        }

        // Scroll indicator
        if (totalRows > startRow + visibleRows)
        {
            var remaining = totalRows - startRow - visibleRows;
            _helpers.WriteLine($"{Colors.Fg256DarkGray}  \u2193 {remaining} more row{(remaining == 1 ? "" : "s")} below{Colors.Reset}");
        }
    }

    /// <summary>
    /// Renders the launcher-specific footer with separator and keyboard hints.
    /// At narrow widths (&lt;60 cols), abbreviates to essential hints only.
    /// </summary>
    public void RenderFooter(int width)
    {
        // Thin separator
        var separatorWidth = Math.Max(1, width - 1);
        _helpers.WriteLine($"{Colors.Fg256DarkGray}{new string('\u2500', separatorWidth)}{Colors.Reset}");

        // Keyboard hints: keys in White, action labels in DarkGray
        string hints;
        if (width < 60)
        {
            // Abbreviated mode
            hints = $"{Colors.Fg256White}h/j/k/l{Colors.Reset} {Colors.Fg256DarkGray}navigate{Colors.Reset}  " +
                    $"{Colors.Fg256White}Enter{Colors.Reset} {Colors.Fg256DarkGray}open{Colors.Reset}  " +
                    $"{Colors.Fg256White}q{Colors.Reset} {Colors.Fg256DarkGray}quit{Colors.Reset}";
        }
        else
        {
            hints = $"{Colors.Fg256White}h/j/k/l{Colors.Reset} {Colors.Fg256DarkGray}navigate{Colors.Reset}  " +
                    $"{Colors.Fg256White}Enter{Colors.Reset} {Colors.Fg256DarkGray}open{Colors.Reset}  " +
                    $"{Colors.Fg256White}a{Colors.Reset} {Colors.Fg256DarkGray}add{Colors.Reset}  " +
                    $"{Colors.Fg256White}d{Colors.Reset} {Colors.Fg256DarkGray}delete{Colors.Reset}  " +
                    $"{Colors.Fg256White}c{Colors.Reset} {Colors.Fg256DarkGray}collections{Colors.Reset}  " +
                    $"{Colors.Fg256White}:{Colors.Reset}{Colors.Fg256DarkGray}cmd{Colors.Reset}  " +
                    $"{Colors.Fg256White}q{Colors.Reset} {Colors.Fg256DarkGray}quit{Colors.Reset}";
        }

        _helpers.WriteLine($" {hints}");
    }

    private static string BuildCellContent(List<Bookmark> bookmarks, int itemIdx, int selectedIndex, int cellWidth, int totalLines, int lineIdx)
    {
        var totalItems = bookmarks.Count + 1;
        if (itemIdx >= totalItems)
        {
            return new string(' ', cellWidth);
        }

        var isCollections = itemIdx == bookmarks.Count;
        var isSelected = itemIdx == selectedIndex;

        string name;
        string domain;

        if (isCollections)
        {
            name = "Collections";
            domain = "saved links";
        }
        else
        {
            var bookmark = bookmarks[itemIdx];
            name = bookmark.Name;
            domain = ExtractDomain(bookmark.Url);
        }

        // Name starts 6 chars from left edge (indent)
        const int indent = 6;
        var nameLineIdx = Math.Max(1, (totalLines - 1) / 2);
        var domainLineIdx = nameLineIdx + 1;
        var textWidth = Math.Max(1, cellWidth - indent - 1); // -1 for right padding
        var barHeight = Math.Min(3, totalLines);

        // Bar lines: centered around name/domain lines
        var barStart = nameLineIdx;
        var barEnd = barStart + barHeight - 1;

        if (isSelected)
        {
            var sb = new System.Text.StringBuilder();

            // Cyan ▌ left bar on content lines
            var showBar = lineIdx >= barStart && lineIdx <= barEnd;

            if (lineIdx == nameLineIdx)
            {
                var truncName = RenderHelpers.TruncateText(name, textWidth);
                if (showBar)
                {
                    sb.Append($"{Colors.Fg256Cyan}\u258c{Colors.Reset}");
                }
                else
                {
                    sb.Append(' ');
                }

                // Reverse video on name (bar or space is always 1 char wide)
                var paddedName = truncName.PadRight(cellWidth - 1);
                sb.Append($"\x1b[30;47m{new string(' ', indent - 1)}{paddedName}\x1b[0m");
            }
            else if (lineIdx == domainLineIdx)
            {
                var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
                if (showBar)
                {
                    sb.Append($"{Colors.Fg256Cyan}\u258c{Colors.Reset}");
                }
                else
                {
                    sb.Append(' ');
                }

                // Brighter White domain text (no reverse video)
                var domainContent = $"{Colors.Fg256White}{truncDomain}{Colors.Reset}";
                var domainPad = Math.Max(0, cellWidth - indent - truncDomain.Length);
                sb.Append($"{new string(' ', indent - 1)}{domainContent}{new string(' ', domainPad)}");
            }
            else
            {
                if (showBar)
                {
                    sb.Append($"{Colors.Fg256Cyan}\u258c{Colors.Reset}");
                    sb.Append(new string(' ', cellWidth - 1));
                }
                else
                {
                    sb.Append(new string(' ', cellWidth));
                }
            }

            return sb.ToString();
        }
        else
        {
            if (lineIdx == nameLineIdx)
            {
                var truncName = RenderHelpers.TruncateText(name, textWidth);
                var pad = Math.Max(0, cellWidth - indent - truncName.Length);
                return $"{new string(' ', indent)}{Colors.Fg256White}{truncName}{Colors.Reset}{new string(' ', pad)}";
            }

            if (lineIdx == domainLineIdx)
            {
                var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
                var pad = Math.Max(0, cellWidth - indent - truncDomain.Length);
                return $"{new string(' ', indent)}{Colors.Fg256DarkGray}{truncDomain}{Colors.Reset}{new string(' ', pad)}";
            }

            return new string(' ', cellWidth);
        }
    }

    internal static string ExtractDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return url;
        }
    }

    // Convenience aliases for color constants
    private static class Colors
    {
        public const string Reset = RenderHelpers.Colors.Reset;
        public const string Fg256Cyan = RenderHelpers.Colors.Fg256Cyan;
        public const string Fg256White = RenderHelpers.Colors.Fg256White;
        public const string Fg256DarkGray = RenderHelpers.Colors.Fg256DarkGray;
    }
}
