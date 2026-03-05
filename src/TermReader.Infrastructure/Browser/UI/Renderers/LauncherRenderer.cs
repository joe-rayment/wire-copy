// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the launcher home screen with a fixed-height 2-column grid layout.
/// </summary>
internal class LauncherRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";

    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public LauncherRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    public void RenderLauncher(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int scrollOffset,
        RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight);

        RenderHeader(bookmarks.Count, layout.Width, p);

        var totalItems = bookmarks.Count + 1;

        if (bookmarks.Count == 0)
        {
            RenderEmptyState(layout.Width, options.TerminalHeight, p);
            return;
        }

        var totalRows = (totalItems + layout.Columns - 1) / layout.Columns;
        var startRow = scrollOffset;
        var endRow = Math.Min(startRow + layout.VisibleRows, totalRows);
        var hasMoreAbove = startRow > 0;
        var hasMoreBelow = totalRows > endRow;
        var aboveCount = startRow * layout.Columns;
        var belowCount = totalItems - (endRow * layout.Columns);

        for (var row = startRow; row < endRow; row++)
        {
            var isFirstVisible = row == startRow;
            var isLastVisible = row == endRow - 1;

            for (var line = 0; line < layout.CellHeight; line++)
            {
                if (isFirstVisible && line == 0 && hasMoreAbove)
                {
                    RenderScrollIndicator(layout.Width, p, true, aboveCount);
                    continue;
                }

                if (isLastVisible && line == layout.CellHeight - 1 && hasMoreBelow)
                {
                    RenderScrollIndicator(layout.Width, p, false, belowCount);
                    continue;
                }

                var sb = new System.Text.StringBuilder();
                var leftIdx = row * layout.Columns;
                sb.Append(BuildCellLine(
                    bookmarks,
                    leftIdx,
                    selectedIndex,
                    layout.CellWidth,
                    layout.CellHeight,
                    line,
                    p));

                if (layout.Columns == 2)
                {
                    sb.Append($"{p.SecondaryText.AnsiFg}\u2502{Reset}");
                    var rightIdx = leftIdx + 1;
                    var rightWidth = layout.Width - layout.CellWidth - 1;
                    if (rightIdx < totalItems)
                    {
                        sb.Append(BuildCellLine(
                            bookmarks,
                            rightIdx,
                            selectedIndex,
                            rightWidth,
                            layout.CellHeight,
                            line,
                            p));
                    }
                    else
                    {
                        sb.Append(new string(' ', rightWidth));
                    }
                }

                _helpers.WriteLine(sb.ToString());
            }
        }
    }

    /// <summary>
    /// Renders the launcher-specific footer with kbd-style keyboard hints.
    /// </summary>
    public void RenderFooter(int width)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}{new string('\u2500', Math.Max(1, width))}{Reset}");

        string hints;
        var version = $"{p.SecondaryText.AnsiFg}{Dim}v1.0{Reset}";
        var versionTextLen = "v1.0".Length;
        var narrow = width < 60;

        if (narrow)
        {
            hints = FormatKbdHint("hjkl", "navigate", p) + "  " +
                    FormatKbdHint("Enter", "open", p) + "  " +
                    FormatKbdHint("q", "quit", p);
        }
        else
        {
            hints = FormatKbdHint("hjkl", "navigate", p) + "  " +
                    FormatKbdHint("Enter", "open", p) + "  " +
                    FormatKbdHint("a", "add", p) + "  " +
                    FormatKbdHint("d", "delete", p) + "  " +
                    FormatKbdHint("q", "quit", p);
        }

        var hintsTextLen = CalculateKbdHintsTextLen(narrow);
        var versionPad = Math.Max(1, width - 1 - hintsTextLen - versionTextLen);
        _helpers.WriteLine($" {hints}{new string(' ', versionPad)}{version}");
    }

    /// <summary>
    /// Computes shared layout parameters from terminal dimensions.
    /// Used by both the renderer and <see cref="CommandHandlers.LauncherCommandHandler"/>.
    /// </summary>
    internal static LauncherLayout ComputeLayout(int terminalWidth, int terminalHeight)
    {
        const int headerLines = 2;
        const int footerLines = 2;
        const int columnThreshold = 40;
        const int standardCellHeight = 5;
        const int compactCellHeight = 3;

        var width = Math.Min(terminalWidth, Console.WindowWidth - 2);
        var columns = width >= columnThreshold ? 2 : 1;
        var availableHeight = Math.Max(4, terminalHeight - headerLines - footerLines);
        var cellHeight = availableHeight < 15 ? compactCellHeight : standardCellHeight;
        var visibleRows = Math.Max(1, availableHeight / cellHeight);
        var cellWidth = columns == 1 ? width : (width - 1) / 2;

        return new LauncherLayout(
            Width: width,
            Columns: columns,
            CellHeight: cellHeight,
            VisibleRows: visibleRows,
            CellWidth: cellWidth,
            HeaderLines: headerLines,
            FooterLines: footerLines);
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

    private static string BuildCellLine(
        List<Bookmark> bookmarks,
        int itemIdx,
        int selectedIndex,
        int cellWidth,
        int cellHeight,
        int lineIdx,
        ThemePalette p)
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

        // For 5-line cells: 0=pad, 1=name, 2=domain, 3=accent, 4=pad
        // For 3-line cells: 0=name, 1=domain, 2=blank
        var nameLineIdx = cellHeight == 5 ? 1 : 0;
        var domainLineIdx = nameLineIdx + 1;
        var accentEndIdx = nameLineIdx + 2;

        const int indent = 6;
        var textWidth = Math.Max(1, cellWidth - indent - 1);
        var showAccent = lineIdx >= nameLineIdx && lineIdx <= accentEndIdx;

        if (isSelected)
        {
            return BuildSelectedLine(
                lineIdx,
                nameLineIdx,
                domainLineIdx,
                showAccent,
                name,
                domain,
                badge,
                indent,
                textWidth,
                cellWidth,
                p);
        }

        return BuildNormalLine(
            lineIdx,
            nameLineIdx,
            domainLineIdx,
            name,
            domain,
            badge,
            indent,
            textWidth,
            cellWidth,
            p);
    }

    private static string BuildSelectedLine(
        int lineIdx,
        int nameLineIdx,
        int domainLineIdx,
        bool showAccent,
        string name,
        string domain,
        string badge,
        int indent,
        int textWidth,
        int cellWidth,
        ThemePalette p)
    {
        var sb = new System.Text.StringBuilder();
        var selFg = p.SelectedItemFg.AnsiFg;
        var selBg = p.SelectedItemBg.AnsiBg;
        var accentFg = p.HeaderBorderFg.AnsiFg;

        // Accent bar column (1 char) — not highlighted
        if (showAccent)
        {
            sb.Append($"{accentFg}\u258c{Reset}");
        }
        else
        {
            sb.Append(' ');
        }

        // Remaining width gets full highlight background on every line
        var contentWidth = cellWidth - 1;

        if (lineIdx == nameLineIdx)
        {
            var truncName = RenderHelpers.TruncateText(name, textWidth);

            if (badge.Length > 0)
            {
                var badgePad = indent - badge.Length - 2;
                sb.Append($"{selBg} {selFg}{badge}");
                sb.Append($"{new string(' ', Math.Max(0, badgePad))}");
                sb.Append($"{Bold}{truncName}{Reset}");
                sb.Append($"{selBg}{new string(' ', Math.Max(0, contentWidth - indent - truncName.Length))}{Reset}");
            }
            else
            {
                sb.Append($"{selBg}{selFg}{new string(' ', indent - 1)}");
                sb.Append($"{Bold}{truncName}{Reset}");
                sb.Append($"{selBg}{new string(' ', Math.Max(0, contentWidth - indent - truncName.Length))}{Reset}");
            }
        }
        else if (lineIdx == domainLineIdx)
        {
            var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
            sb.Append($"{selBg}{selFg}{new string(' ', indent - 1)}");
            sb.Append($"{truncDomain}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - indent - truncDomain.Length))}{Reset}");
        }
        else
        {
            // Padding lines — full highlight background
            sb.Append($"{selBg}{new string(' ', contentWidth)}{Reset}");
        }

        return sb.ToString();
    }

    private static string BuildNormalLine(
        int lineIdx,
        int nameLineIdx,
        int domainLineIdx,
        string name,
        string domain,
        string badge,
        int indent,
        int textWidth,
        int cellWidth,
        ThemePalette p)
    {
        if (lineIdx == nameLineIdx)
        {
            var truncName = RenderHelpers.TruncateText(name, textWidth);
            var pad = Math.Max(0, cellWidth - indent - truncName.Length);

            if (badge.Length > 0)
            {
                var badgePad = indent - badge.Length - 1;
                return $" {p.SecondaryText.AnsiFg}{badge}{Reset}{new string(' ', badgePad)}" +
                       $"{p.PrimaryText.AnsiFg}{Bold}{truncName}{Reset}{new string(' ', pad)}";
            }

            return $"{new string(' ', indent)}{p.PrimaryText.AnsiFg}{Bold}{truncName}{Reset}{new string(' ', pad)}";
        }

        if (lineIdx == domainLineIdx)
        {
            var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
            var pad = Math.Max(0, cellWidth - indent - truncDomain.Length);
            return $"{new string(' ', indent)}{p.SecondaryText.AnsiFg}{Dim}{truncDomain}{Reset}{new string(' ', pad)}";
        }

        return new string(' ', cellWidth);
    }

    private static string FormatKbdHint(string key, string action, ThemePalette p)
    {
        return $"{p.SecondaryText.AnsiFg}[{Reset}{p.PrimaryText.AnsiFg}{key}{Reset}{p.SecondaryText.AnsiFg}]{Reset}" +
               $" {p.SecondaryText.AnsiFg}{Dim}{action}{Reset}";
    }

    private static int CalculateKbdHintsTextLen(bool narrow)
    {
        if (narrow)
        {
            return "[hjkl] navigate  [Enter] open  [q] quit".Length;
        }

        return "[hjkl] navigate  [Enter] open  [a] add  [d] delete  [q] quit".Length;
    }

    private void RenderHeader(int bookmarkCount, int width, ThemePalette p)
    {
        var titleText = "TermReader";
        var rightText = $"{bookmarkCount} bookmarks  v1.0";
        var pad = Math.Max(1, width - titleText.Length - rightText.Length - 2);

        var title = $"{p.HeaderTitleFg.AnsiFg}{Bold}{titleText}{Reset}";
        var countPart = $"{p.SecondaryText.AnsiFg}{bookmarkCount} bookmarks{Reset}";
        var versionPart = $"{p.SecondaryText.AnsiFg}{Dim}v1.0{Reset}";

        _helpers.WriteLine($" {title}{new string(' ', pad)}{countPart}  {versionPart} ");
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}{new string('\u2500', width)}{Reset}");
    }

    private void RenderEmptyState(int width, int terminalHeight, ThemePalette p)
    {
        var availableLines = Math.Max(6, terminalHeight - 4);
        var topPad = Math.Max(1, availableLines / 3);

        for (var i = 0; i < topPad; i++)
        {
            _helpers.WriteLine();
        }

        var heading = "No bookmarks yet";
        var headingPad = Math.Max(0, (width - heading.Length) / 2);
        _helpers.WriteLine($"{new string(' ', headingPad)}{p.PrimaryText.AnsiFg}{Bold}{heading}{Reset}");
        _helpers.WriteLine();

        var instruction = "Press [a] to add your first site";
        var instrPad = Math.Max(0, (width - instruction.Length) / 2);
        var instrFormatted = $"Press {p.SecondaryText.AnsiFg}[{Reset}{p.PrimaryText.AnsiFg}a{Reset}{p.SecondaryText.AnsiFg}]{Reset}" +
                             $"{p.SecondaryText.AnsiFg}{Dim} to add your first site{Reset}";
        _helpers.WriteLine($"{new string(' ', instrPad)}{instrFormatted}");
        _helpers.WriteLine();

        var collLabel = "[c]  Collections";
        var collPad = Math.Max(0, (width - collLabel.Length) / 2);
        _helpers.WriteLine(
            $"{new string(' ', collPad)}{p.SecondaryText.AnsiFg}[{Reset}{p.PrimaryText.AnsiFg}c{Reset}{p.SecondaryText.AnsiFg}]{Reset}" +
            $"  {p.PrimaryText.AnsiFg}Collections{Reset}");

        var domainPad = Math.Max(0, (width - "saved links".Length) / 2);
        _helpers.WriteLine($"{new string(' ', domainPad + 5)}{p.SecondaryText.AnsiFg}{Dim}saved links{Reset}");
    }

    private void RenderScrollIndicator(int width, ThemePalette p, bool isUp, int count)
    {
        var arrow = isUp ? "\u25b2" : "\u25bc";
        var direction = isUp ? "above" : "below";
        var indicator = $"{arrow} {count} more {direction}";
        var indicatorPad = Math.Max(0, (width - indicator.Length) / 2);
        _helpers.WriteLine(
            $"{new string(' ', indicatorPad)}{p.SecondaryText.AnsiFg}{Dim}{indicator}{Reset}" +
            $"{new string(' ', Math.Max(0, width - indicatorPad - indicator.Length))}");
    }
}
