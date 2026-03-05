// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the launcher home screen with a full-viewport grid layout.
/// Items expand to fill available space with thin separators between cells.
/// </summary>
internal class LauncherRenderer
{
    private const string Reset = "\x1b[0m";
    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public LauncherRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    public void RenderLauncher(List<Bookmark> bookmarks, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        // Header: TermReader left + bookmark count + version right (1 line + separator)
        var title = $"{p.HeaderTitleFg.AnsiFg}TermReader{Reset}";
        var statusInfo = $"{p.SecondaryText.AnsiFg}{bookmarks.Count} bookmarks{Reset}";
        var version = $"{p.SecondaryText.AnsiFg}v1.0{Reset}";
        var headerRight = $"{statusInfo} {version}";

        var titleTextLen = "TermReader".Length;
        var rightTextLen = $"{bookmarks.Count} bookmarks v1.0".Length;
        var padding = Math.Max(1, width - titleTextLen - rightTextLen - 2);
        _helpers.WriteLine($" {title}{new string(' ', padding)}{headerRight} ");

        _helpers.WriteLine($"{p.SecondaryText.AnsiFg}{new string('\u2500', width)}{Reset}");

        var headerLines = _helpers.LinesWritten;
        var footerLines = 2;
        var availableHeight = Math.Max(4, options.TerminalHeight - headerLines - footerLines);

        var columns = width >= 35 ? 2 : 1;
        var totalItems = bookmarks.Count + 1;

        if (bookmarks.Count == 0 && totalItems == 1)
        {
            _helpers.WriteLine();
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}No bookmarks. Press 'a' to add one.{Reset}");
            _helpers.WriteLine();
            _helpers.WriteLine($"  {p.HeaderBorderFg.AnsiFg}\u2502{Reset} {p.PrimaryText.AnsiFg}Collections{Reset}");
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
                    _helpers.WriteLine($"{p.SecondaryText.AnsiFg}{new string('\u2500', halfSep)}\u253c{new string('\u2500', width - halfSep - 1)}{Reset}");
                }
                else
                {
                    _helpers.WriteLine($"{p.SecondaryText.AnsiFg}{new string('\u2500', width)}{Reset}");
                }
            }

            var leftIdx = row * columns;
            var rightIdx = columns == 2 ? leftIdx + 1 : -1;

            var contentLines = rowHeight;

            for (var line = 0; line < contentLines; line++)
            {
                var sb = new System.Text.StringBuilder();

                // Left cell
                sb.Append(BuildCellContent(bookmarks, leftIdx, selectedIndex, cellWidth, contentLines, line, p));

                // Vertical separator + right cell
                if (columns == 2)
                {
                    sb.Append($"{p.SecondaryText.AnsiFg}\u2502{Reset}");

                    if (rightIdx >= 0 && rightIdx < totalItems)
                    {
                        sb.Append(BuildCellContent(bookmarks, rightIdx, selectedIndex, width - cellWidth - 1, contentLines, line, p));
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
            _helpers.WriteLine($"{p.SecondaryText.AnsiFg}  \u2193 {remaining} more row{(remaining == 1 ? string.Empty : "s")} below{Reset}");
        }
    }

    /// <summary>
    /// Renders the launcher-specific footer with separator and keyboard hints.
    /// At narrow widths (&lt;60 cols), abbreviates to essential hints only.
    /// </summary>
    public void RenderFooter(int width)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var separatorWidth = Math.Max(1, width - 1);
        _helpers.WriteLine($"{p.StatusBarSeparatorFg.AnsiFg}{new string('\u2500', separatorWidth)}{Reset}");

        string hints;
        if (width < 60)
        {
            hints = $"{p.PrimaryText.AnsiFg}h/j/k/l{Reset} {p.SecondaryText.AnsiFg}navigate{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}Enter{Reset} {p.SecondaryText.AnsiFg}open{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}q{Reset} {p.SecondaryText.AnsiFg}quit{Reset}";
        }
        else
        {
            hints = $"{p.PrimaryText.AnsiFg}h/j/k/l{Reset} {p.SecondaryText.AnsiFg}navigate{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}Enter{Reset} {p.SecondaryText.AnsiFg}open{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}a{Reset} {p.SecondaryText.AnsiFg}add{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}d{Reset} {p.SecondaryText.AnsiFg}delete{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}c{Reset} {p.SecondaryText.AnsiFg}collections{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}:{Reset}{p.SecondaryText.AnsiFg}cmd{Reset}  " +
                    $"{p.PrimaryText.AnsiFg}q{Reset} {p.SecondaryText.AnsiFg}quit{Reset}";
        }

        _helpers.WriteLine($" {hints}");
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

    private static string BuildCellContent(List<Bookmark> bookmarks, int itemIdx, int selectedIndex, int cellWidth, int totalLines, int lineIdx, ThemePalette p)
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

        string badge;
        if (isCollections)
        {
            badge = "[c]";
        }
        else if (itemIdx < 9)
        {
            badge = $"[{itemIdx + 1}]";
        }
        else
        {
            badge = string.Empty;
        }

        const int indent = 6;
        var nameLineIdx = Math.Max(1, (totalLines - 1) / 2);
        var domainLineIdx = nameLineIdx + 1;
        var textWidth = Math.Max(1, cellWidth - indent - 1);
        var barHeight = Math.Min(3, totalLines);

        var barStart = nameLineIdx;
        var barEnd = barStart + barHeight - 1;

        var selFg = p.SelectedItemFg.AnsiFg;
        var selBg = p.SelectedItemBg.AnsiBg;

        if (isSelected)
        {
            var sb = new System.Text.StringBuilder();
            var showBar = lineIdx >= barStart && lineIdx <= barEnd;

            if (lineIdx == nameLineIdx)
            {
                var truncName = RenderHelpers.TruncateText(name, textWidth);
                if (showBar)
                {
                    sb.Append($"{p.HeaderBorderFg.AnsiFg}\u258c\x1b[0m");
                }
                else
                {
                    sb.Append(' ');
                }

                var indentContent = badge.Length > 0
                    ? $" {p.SecondaryText.AnsiFg}{badge}\x1b[0m{selFg}{selBg}{new string(' ', indent - 1 - badge.Length - 1)}"
                    : $"{selFg}{selBg}{new string(' ', indent - 1)}";
                var paddedName = truncName.PadRight(cellWidth - indent);
                sb.Append($"{indentContent}{paddedName}\x1b[0m");
            }
            else if (lineIdx == domainLineIdx)
            {
                var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
                if (showBar)
                {
                    sb.Append($"{p.HeaderBorderFg.AnsiFg}\u258c\x1b[0m");
                }
                else
                {
                    sb.Append(' ');
                }

                var domainContent = $"{p.PrimaryText.AnsiFg}{truncDomain}\x1b[0m";
                var domainPad = Math.Max(0, cellWidth - indent - truncDomain.Length);
                sb.Append($"{new string(' ', indent - 1)}{domainContent}{new string(' ', domainPad)}");
            }
            else
            {
                if (showBar)
                {
                    sb.Append($"{p.HeaderBorderFg.AnsiFg}\u258c\x1b[0m");
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

                if (badge.Length > 0)
                {
                    var badgePad = indent - badge.Length - 1;
                    return $" {p.SecondaryText.AnsiFg}{badge}\x1b[0m{new string(' ', badgePad)}{p.PrimaryText.AnsiFg}{truncName}\x1b[0m{new string(' ', pad)}";
                }

                return $"{new string(' ', indent)}{p.PrimaryText.AnsiFg}{truncName}\x1b[0m{new string(' ', pad)}";
            }

            if (lineIdx == domainLineIdx)
            {
                var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
                var pad = Math.Max(0, cellWidth - indent - truncDomain.Length);
                return $"{new string(' ', indent)}{p.SecondaryText.AnsiFg}{truncDomain}\x1b[0m{new string(' ', pad)}";
            }

            return new string(' ', cellWidth);
        }
    }
}
