// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the launcher home screen with layout variant support:
/// Grid (2-column cards), List (single-column rows), Compact (3-column mini-cards).
/// </summary>
internal class LauncherRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";

    // Block-letter wordmark using ▄ ▀ █ half-block characters for smooth 2-row-tall text.
    // Each character is ~4 cols wide. Total width ~46 chars for "TermReader".
    private static readonly string[] Wordmark =
    [
        "▀█▀ █▀▀ █▀█ █▀▄▀█ █▀█ █▀▀ ▄▀█ █▀▄ █▀▀ █▀█",
        " █  ██▄ █▀▄ █ ▀ █ █▀▄ ██▄ █▀█ █▄▀ ██▄ █▀▄",
    ];

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
        var variant = options.LayoutVariant ?? "Grid";

        switch (variant)
        {
            case "List":
                RenderListVariant(bookmarks, selectedIndex, scrollOffset, options);
                break;
            case "Compact":
                RenderCompactVariant(bookmarks, selectedIndex, scrollOffset, options);
                break;
            default:
                RenderGridVariant(bookmarks, selectedIndex, scrollOffset, options);
                break;
        }
    }

    /// <summary>
    /// Renders the launcher-specific footer with kbd-style keyboard hints.
    /// </summary>
    public void RenderFooter(int width)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        var hints = FormatKbdHint("Enter", "open", p) + "  " +
                    FormatKbdHint("o", "go to url", p) + "  " +
                    FormatKbdHint("a", "add", p) + "  " +
                    FormatKbdHint("d", "delete", p) + "  " +
                    FormatKbdHint("?", "help", p);

        var version = $"{p.GetMutedFg().AnsiFg}{Dim}v1.0{Reset}";
        var versionTextLen = "v1.0".Length;

        // [Enter] open  [o] go to url  [a] add  [d] delete  [?] help
        var hintsTextLen = "[Enter] open  [o] go to url  [a] add  [d] delete  [?] help".Length;
        var versionPad = Math.Max(1, width - 1 - hintsTextLen - versionTextLen);
        _helpers.WriteLine($" {hints}{new string(' ', versionPad)}{version}");
    }

    /// <summary>
    /// Computes shared layout parameters from terminal dimensions.
    /// Used by both the renderer and <see cref="CommandHandlers.LauncherCommandHandler"/>.
    /// </summary>
    internal static LauncherLayout ComputeLayout(int terminalWidth, int terminalHeight)
    {
        return ComputeLayout(terminalWidth, terminalHeight, "Grid");
    }

    /// <summary>
    /// Computes shared layout parameters from terminal dimensions and layout variant.
    /// </summary>
    internal static LauncherLayout ComputeLayout(int terminalWidth, int terminalHeight, string variant)
    {
        // Large wordmark: border + pad + 2 art + subtitle + pad + border = 7
        // Narrow: border + title + subtitle + pad + border = 5
        var headerLines = terminalWidth >= 56 ? 7 : 5;
        const int urlBarLines = 5;
        const int footerLines = 2;

        var width = Math.Max(1, terminalWidth - 2);
        var availableHeight = Math.Max(4, terminalHeight - headerLines - urlBarLines - footerLines);

        int columns;
        int cellHeight;

        switch (variant)
        {
            case "List":
                columns = 1;
                cellHeight = 1;
                break;

            case "Compact":
            {
                var baseColumns = width >= 40 ? 2 : 1;
                columns = width >= 60 ? 3 : baseColumns;
                cellHeight = 3;
                break;
            }

            default: // Grid
            {
                const int columnThreshold = 40;
                const int standardCellHeight = 5;
                const int compactCellHeight = 3;
                columns = width >= columnThreshold ? 2 : 1;
                cellHeight = availableHeight < 15 ? compactCellHeight : standardCellHeight;
                break;
            }
        }

        var visibleRows = Math.Max(1, availableHeight / cellHeight);
        var cellWidth = columns <= 1
            ? width
            : Math.Max(1, (width - (columns - 1)) / columns);

        return new LauncherLayout(
            Width: width,
            Columns: columns,
            CellHeight: cellHeight,
            VisibleRows: visibleRows,
            CellWidth: cellWidth,
            HeaderLines: headerLines + urlBarLines,
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
            name = "READING LIST";
            domain = "reading list";
        }
        else
        {
            var bookmark = bookmarks[itemIdx];
            name = bookmark.Name.ToUpperInvariant();
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

        // Accent bar column (1 char) — not highlighted
        if (showAccent)
        {
            sb.Append(Selection.AccentBar(p));
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
                sb.Append($"{Bold}{selFg}{selBg}{truncName}");
                sb.Append($"{new string(' ', Math.Max(0, contentWidth - indent + 1 - truncName.Length))}{Reset}");
            }
            else
            {
                sb.Append($"{selBg}{selFg}{new string(' ', indent - 1)}");
                sb.Append($"{Bold}{selFg}{selBg}{truncName}");
                sb.Append($"{new string(' ', Math.Max(0, contentWidth - indent + 1 - truncName.Length))}{Reset}");
            }
        }
        else if (lineIdx == domainLineIdx)
        {
            var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
            sb.Append($"{selBg}{p.SecondaryText.AnsiFg}{new string(' ', indent - 1)}");
            sb.Append($"{truncDomain}");
            sb.Append($"{new string(' ', Math.Max(0, contentWidth - indent + 1 - truncDomain.Length))}{Reset}");
        }
        else
        {
            // Padding lines — no background fill, just empty space
            sb.Append(new string(' ', contentWidth));
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
                return $" {p.GetAccentFg().AnsiFg}{badge}{Reset}{new string(' ', badgePad)}" +
                       $"{Bold}{p.PrimaryText.AnsiFg}{truncName}{Reset}{new string(' ', pad)}";
            }

            return $"{new string(' ', indent)}{Bold}{p.PrimaryText.AnsiFg}{truncName}{Reset}{new string(' ', pad)}";
        }

        if (lineIdx == domainLineIdx)
        {
            var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
            var pad = Math.Max(0, cellWidth - indent - truncDomain.Length);
            return $"{new string(' ', indent)}{p.SecondaryText.AnsiFg}{truncDomain}{Reset}{new string(' ', pad)}";
        }

        return new string(' ', cellWidth);
    }

    /// <summary>
    /// Builds a single line for the List layout variant.
    /// Format: " [n] NAME                    domain.com"
    /// Selected: "▌[n] NAME                    domain.com" with highlight bg.
    /// </summary>
    private static string BuildListLine(
        List<Bookmark> bookmarks,
        int itemIdx,
        int selectedIndex,
        int width,
        ThemePalette p)
    {
        var totalItems = bookmarks.Count + 1;
        if (itemIdx >= totalItems)
        {
            return new string(' ', width);
        }

        var isCollections = itemIdx == bookmarks.Count;
        var isSelected = itemIdx == selectedIndex;

        string name;
        string domain;

        if (isCollections)
        {
            name = "\u2605 READING LIST";
            domain = "reading list";
        }
        else
        {
            var bookmark = bookmarks[itemIdx];
            name = bookmark.Name.ToUpperInvariant();
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
            badge = "   ";
        }

        // Layout:  [n] NAME            domain
        // Widths:  1 + badge(3) + 1 + name(variable) + gap(>=2) + domain + 1
        const int badgeWidth = 3;
        const int gapMin = 2;
        var domainMaxWidth = Math.Min(domain.Length, (width - badgeWidth - gapMin - 3) / 3);
        var truncDomain = RenderHelpers.TruncateText(domain, domainMaxWidth);
        var nameMaxWidth = Math.Max(1, width - badgeWidth - 2 - gapMin - truncDomain.Length - 1);
        var truncName = RenderHelpers.TruncateText(name, nameMaxWidth);
        var gap = Math.Max(0, width - 2 - badgeWidth - truncName.Length - truncDomain.Length - 1);

        if (isSelected)
        {
            var selFg = p.SelectedItemFg.AnsiFg;
            var selBg = p.SelectedItemBg.AnsiBg;
            var sb = new System.Text.StringBuilder();
            sb.Append(Selection.AccentBar(p));
            sb.Append($"{selBg}{selFg}{badge} ");
            sb.Append($"{Bold}{selFg}{selBg}{truncName}{Reset}");
            sb.Append($"{selBg}{new string(' ', gap)}");
            sb.Append($"{p.SecondaryText.AnsiFg}{selBg}{truncDomain}");
            sb.Append($" {Reset}");
            return sb.ToString();
        }

        var sb2 = new System.Text.StringBuilder();
        sb2.Append($" {p.GetAccentFg().AnsiFg}{badge}{Reset} ");
        sb2.Append($"{Bold}{p.PrimaryText.AnsiFg}{truncName}{Reset}");
        sb2.Append(new string(' ', gap));
        sb2.Append($"{p.SecondaryText.AnsiFg}{truncDomain}{Reset}");
        sb2.Append(' ');
        return sb2.ToString();
    }

    /// <summary>
    /// Builds a single line within a Compact layout cell.
    /// 3-line cells: line 0 = badge+name, line 1 = domain, line 2 = blank separator.
    /// </summary>
    private static string BuildCompactCellLine(
        List<Bookmark> bookmarks,
        int itemIdx,
        int selectedIndex,
        int cellWidth,
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
            name = "\u2605LIST";
            domain = "reading list";
        }
        else
        {
            var bookmark = bookmarks[itemIdx];

            // Abbreviate name to fit compact cells
            name = bookmark.Name.ToUpperInvariant();
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

        // For compact 3-line cells: 0=badge+name, 1=domain, 2=blank
        const int indent = 1;
        var textWidth = Math.Max(1, cellWidth - indent - 1);

        if (isSelected)
        {
            return BuildCompactSelectedLine(lineIdx, name, domain, badge, textWidth, cellWidth, p);
        }

        return BuildCompactNormalLine(lineIdx, name, domain, badge, indent, textWidth, cellWidth, p);
    }

    private static string BuildCompactSelectedLine(
        int lineIdx,
        string name,
        string domain,
        string badge,
        int textWidth,
        int cellWidth,
        ThemePalette p)
    {
        var selFg = p.SelectedItemFg.AnsiFg;
        var selBg = p.SelectedItemBg.AnsiBg;
        var sb = new System.Text.StringBuilder();

        // Accent bar on lines 0 and 1
        if (lineIdx <= 1)
        {
            sb.Append(Selection.AccentBar(p));
        }
        else
        {
            sb.Append(' ');
        }

        var contentWidth = cellWidth - 1;

        if (lineIdx == 0)
        {
            // Badge + name
            var combined = badge.Length > 0 ? $"{badge} {name}" : name;
            var truncCombined = RenderHelpers.TruncateText(combined, textWidth);
            sb.Append($"{selBg}{selFg}{Bold}{truncCombined}{Reset}");
            sb.Append($"{selBg}{new string(' ', Math.Max(0, contentWidth - truncCombined.Length))}{Reset}");
        }
        else if (lineIdx == 1)
        {
            // Domain
            var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
            sb.Append($"{selBg}{p.SecondaryText.AnsiFg}{Dim} {truncDomain}{Reset}");
            sb.Append($"{selBg}{new string(' ', Math.Max(0, contentWidth - truncDomain.Length - 1))}{Reset}");
        }
        else
        {
            // Blank separator line
            sb.Append(new string(' ', contentWidth));
        }

        return sb.ToString();
    }

    private static string BuildCompactNormalLine(
        int lineIdx,
        string name,
        string domain,
        string badge,
        int indent,
        int textWidth,
        int cellWidth,
        ThemePalette p)
    {
        if (lineIdx == 0)
        {
            // Badge + name
            var combined = badge.Length > 0
                ? $"{p.GetAccentFg().AnsiFg}{badge}{Reset} {Bold}{p.PrimaryText.AnsiFg}{RenderHelpers.TruncateText(name, Math.Max(1, textWidth - badge.Length - 1))}{Reset}"
                : $"{Bold}{p.PrimaryText.AnsiFg}{RenderHelpers.TruncateText(name, textWidth)}{Reset}";

            var combinedPlain = badge.Length > 0
                ? $"{badge} {RenderHelpers.TruncateText(name, Math.Max(1, textWidth - badge.Length - 1))}"
                : RenderHelpers.TruncateText(name, textWidth);

            var pad = Math.Max(0, cellWidth - indent - combinedPlain.Length);
            return $" {combined}{new string(' ', pad)}";
        }

        if (lineIdx == 1)
        {
            // Domain
            var truncDomain = RenderHelpers.TruncateText(domain, Math.Max(1, textWidth - 1));
            var pad = Math.Max(0, cellWidth - indent - truncDomain.Length - 1);
            return $"  {p.SecondaryText.AnsiFg}{Dim}{truncDomain}{Reset}{new string(' ', pad)}";
        }

        // Blank separator line
        return new string(' ', cellWidth);
    }

    private static string FormatKbdHint(string key, string action, ThemePalette p)
    {
        return $"{p.SecondaryText.AnsiFg}[{Reset}{p.GetAccentFg().AnsiFg}{key}{Reset}{p.SecondaryText.AnsiFg}]{Reset} {p.SecondaryText.AnsiFg}{action}{Reset}";
    }

    private void RenderGridVariant(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int scrollOffset,
        RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight);

        RenderHeader(layout.Width, p);
        RenderUrlBar(layout.Width, selectedIndex == -1, p);

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

    private void RenderListVariant(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int scrollOffset,
        RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, "List");

        RenderHeader(layout.Width, p);
        RenderUrlBar(layout.Width, selectedIndex == -1, p);

        var totalItems = bookmarks.Count + 1;

        if (bookmarks.Count == 0)
        {
            RenderEmptyState(layout.Width, options.TerminalHeight, p);
            return;
        }

        var totalRows = totalItems; // 1 item per row
        var startRow = scrollOffset;
        var endRow = Math.Min(startRow + layout.VisibleRows, totalRows);
        var hasMoreAbove = startRow > 0;
        var hasMoreBelow = totalRows > endRow;
        var aboveCount = startRow;
        var belowCount = totalItems - endRow;

        for (var row = startRow; row < endRow; row++)
        {
            if (row == startRow && hasMoreAbove)
            {
                RenderScrollIndicator(layout.Width, p, true, aboveCount);
                continue;
            }

            if (row == endRow - 1 && hasMoreBelow)
            {
                RenderScrollIndicator(layout.Width, p, false, belowCount);
                continue;
            }

            _helpers.WriteLine(BuildListLine(bookmarks, row, selectedIndex, layout.Width, p));
        }
    }

    private void RenderCompactVariant(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int scrollOffset,
        RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, "Compact");

        RenderHeader(layout.Width, p);
        RenderUrlBar(layout.Width, selectedIndex == -1, p);

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

                for (var col = 0; col < layout.Columns; col++)
                {
                    var itemIdx = (row * layout.Columns) + col;
                    var isLastCol = col == layout.Columns - 1;
                    var cellW = isLastCol
                        ? layout.Width - (layout.CellWidth * (layout.Columns - 1)) - (layout.Columns - 1)
                        : layout.CellWidth;

                    if (col > 0)
                    {
                        sb.Append($"{p.SecondaryText.AnsiFg}\u2502{Reset}");
                    }

                    if (itemIdx < totalItems)
                    {
                        sb.Append(BuildCompactCellLine(
                            bookmarks, itemIdx, selectedIndex, cellW, line, p));
                    }
                    else
                    {
                        sb.Append(new string(' ', cellW));
                    }
                }

                _helpers.WriteLine(sb.ToString());
            }
        }
    }

    private void RenderHeader(int width, ThemePalette p)
    {
        var borderColor = p.GetDimFg().AnsiFg;
        var titleColor = p.HeaderTitleFg.AnsiFg;
        var subtitle = "a better reading experience";
        var wordmarkWidth = Wordmark[0].Length;
        var useLargeWordmark = width >= wordmarkWidth + 6;
        var boxWidth = Math.Min(width - 2, 78);
        var innerWidth = boxWidth - 2;

        // ╭──────────────────────────────────────────────────╮
        _helpers.WriteLine(
            $" {borderColor}\u256d{new string('\u2500', boxWidth)}\u256e{Reset}");

        if (useLargeWordmark)
        {
            // │                                                  │
            _helpers.WriteLine(
                $" {borderColor}\u2502{Reset}{new string(' ', innerWidth + 1)}{borderColor}\u2502{Reset}");

            // │  ▀█▀ █▀▀ █▀█ ...                                │  (wordmark line 1)
            // │   █  ██▄ █▀▄ ...                                │  (wordmark line 2)
            foreach (var wl in Wordmark)
            {
                var leftPad = 2;
                var rightPad = Math.Max(0, innerWidth - wl.Length - leftPad);
                _helpers.WriteLine(
                    $" {borderColor}\u2502{Reset}{new string(' ', leftPad)}" +
                    $"{titleColor}{Bold}{wl}{Reset}" +
                    $"{new string(' ', rightPad + 1)}{borderColor}\u2502{Reset}");
            }
        }
        else
        {
            // Narrow fallback: single-line pink title
            var titlePad = Math.Max(0, innerWidth - "TermReader".Length - 2);
            _helpers.WriteLine(
                $" {borderColor}\u2502{Reset}  {titleColor}{Bold}{"TermReader"}{Reset}" +
                $"{new string(' ', titlePad)} {borderColor}\u2502{Reset}");
        }

        // │  a better reading experience                     │
        var subPad = Math.Max(0, innerWidth - subtitle.Length - 2);
        _helpers.WriteLine(
            $" {borderColor}\u2502{Reset}  {p.SecondaryText.AnsiFg}{subtitle}{Reset}" +
            $"{new string(' ', subPad)} {borderColor}\u2502{Reset}");

        // │                                                  │
        _helpers.WriteLine(
            $" {borderColor}\u2502{Reset}{new string(' ', innerWidth + 1)}{borderColor}\u2502{Reset}");

        // ╰──────────────────────────────────────────────────╯
        _helpers.WriteLine(
            $" {borderColor}\u2570{new string('\u2500', boxWidth)}\u256f{Reset}");
    }

    private void RenderUrlBar(int width, bool isSelected, ThemePalette p)
    {
        // Use ~75% of width, capped at 70, min 30
        var barWidth = Math.Clamp(width * 3 / 4, Math.Min(30, width - 4), 70);
        var pad = Math.Max(0, (width - barWidth) / 2);
        var innerWidth = barWidth - 4;

        var borderColor = isSelected ? p.SelectedItemFg.AnsiFg : p.HeaderBorderFg.AnsiFg;
        var placeholder = isSelected ? "Type a URL and press Enter" : "Go to URL...";
        if (placeholder.Length > innerWidth)
        {
            placeholder = placeholder[..innerWidth];
        }

        var textColor = isSelected
            ? $"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg}"
            : $"{p.SecondaryText.AnsiFg}";

        _helpers.WriteLine();
        _helpers.WriteLine(
            $"{new string(' ', pad)}{borderColor}\u256d{new string('\u2500', barWidth - 2)}\u256e{Reset}");
        _helpers.WriteLine(
            $"{new string(' ', pad)}{borderColor}\u2502 {Reset}" +
            $"{textColor}{placeholder}{new string(' ', Math.Max(0, innerWidth - placeholder.Length))}{Reset}" +
            $"{borderColor} \u2502{Reset}");
        _helpers.WriteLine(
            $"{new string(' ', pad)}{borderColor}\u2570{new string('\u2500', barWidth - 2)}\u256f{Reset}");
        _helpers.WriteLine();
    }

    private void RenderEmptyState(int width, int terminalHeight, ThemePalette p)
    {
        var availableLines = Math.Max(6, terminalHeight - 4);
        var topPad = Math.Max(1, availableLines / 3);

        for (var i = 0; i < topPad; i++)
        {
            _helpers.WriteLine();
        }

        var heading = "Your bookmarks await";
        var headingPad = Math.Max(0, (width - heading.Length) / 2);
        _helpers.WriteLine($"{new string(' ', headingPad)}{p.PrimaryText.AnsiFg}{Bold}{heading}{Reset}");
        _helpers.WriteLine();

        var instruction = "Press [a] to add your first site";
        var instrPad = Math.Max(0, (width - instruction.Length) / 2);
        var instrFormatted = $"Press {p.SecondaryText.AnsiFg}[{Reset}{p.GetAccentFg().AnsiFg}a{Reset}{p.SecondaryText.AnsiFg}]{Reset}" +
                             $"{p.SecondaryText.AnsiFg}{Dim} to add your first site{Reset}";
        _helpers.WriteLine($"{new string(' ', instrPad)}{instrFormatted}");
        _helpers.WriteLine();

        var collLabel = "[c]  READING LIST";
        var collPad = Math.Max(0, (width - collLabel.Length) / 2);
        _helpers.WriteLine(
            $"{new string(' ', collPad)}{p.SecondaryText.AnsiFg}[{Reset}{p.GetAccentFg().AnsiFg}c{Reset}{p.SecondaryText.AnsiFg}]{Reset}" +
            $"  {p.PrimaryText.AnsiFg}{Bold}READING LIST{Reset}");

        var domainPad = Math.Max(0, (width - "reading list".Length) / 2);
        _helpers.WriteLine($"{new string(' ', domainPad + 5)}{p.SecondaryText.AnsiFg}{Dim}reading list{Reset}");
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
