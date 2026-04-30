// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the launcher home screen with layout variant support:
/// Grid (2-column cards), List (single-column rows), Compact (3-column mini-cards).
/// </summary>
internal class LauncherRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";

    private const int WordmarkWidth = 87;

    // 6-row ASCII-art wordmark from wirecopy-design-system/dist/preview/brand-logo.html.
    // Two-tone pink: outer rows (1,2,5,6) in HeaderTitleFg (#ff87d7 ANSI 212),
    // inner rows (3,4) in CelebrationFg (#ff5fd7 ANSI 206) for vertical stripe.
    // Width: 87 columns. Falls back to single-line title when terminal is narrower.
    private static readonly string[] Wordmark =
    [
        " ████████╗███████╗██████╗ ███╗   ███╗  ██████╗ ███████╗ █████╗ ██████╗ ███████╗██████╗ ",
        " ╚══██╔══╝██╔════╝██╔══██╗████╗ ████║  ██╔══██╗██╔════╝██╔══██╗██╔══██╗██╔════╝██╔══██╗",
        "    ██║   █████╗  ██████╔╝██╔████╔██║  ██████╔╝█████╗  ███████║██║  ██║█████╗  ██████╔╝",
        "    ██║   ██╔══╝  ██╔══██╗██║╚██╔╝██║  ██╔══██╗██╔══╝  ██╔══██║██║  ██║██╔══╝  ██╔══██╗",
        "    ██║   ███████╗██║  ██║██║ ╚═╝ ██║  ██║  ██║███████╗██║  ██║██████╔╝███████╗██║  ██║",
        "    ╚═╝   ╚══════╝╚═╝  ╚═╝╚═╝     ╚═╝  ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝╚═════╝ ╚══════╝╚═╝  ╚═╝",
    ];

    // Rows 3 and 4 (zero-indexed 2 and 3) use the darker pink (CelebrationFg) for vertical stripe.
    private static readonly bool[] WordmarkUsesDark = [false, false, true, true, false, false];

    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public LauncherRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    /// <summary>
    /// Renders the launcher. Wordmark, URL bar, and bookmark grid are treated
    /// as a single virtual content stream; <paramref name="scrollOffset"/> is
    /// the number of lines that have scrolled off the top. The footer stays
    /// pinned at the bottom and is rendered separately by the caller.
    /// </summary>
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
                RenderVariant(bookmarks, selectedIndex, scrollOffset, options, "List");
                break;
            case "Compact":
                RenderVariant(bookmarks, selectedIndex, scrollOffset, options, "Compact");
                break;
            default:
                RenderVariant(bookmarks, selectedIndex, scrollOffset, options, "Grid");
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
    /// <remarks>
    /// <see cref="LauncherLayout.VisibleRows"/> is computed for the WORST CASE
    /// (no page scroll, header + URL bar fully visible) so the initial render
    /// fits on screen. The actual viewport when scrolled is dynamic — the
    /// wordmark and URL bar collapse upward as the user scrolls into the
    /// bookmark list. See <see cref="ComputeViewportHeight"/>.
    /// </remarks>
    internal static LauncherLayout ComputeLayout(int terminalWidth, int terminalHeight, string variant)
    {
        // Large wordmark: border + pad + 6 art rows + subtitle + pad + border = 11
        // Narrow: border + title + subtitle + pad + border = 5
        // Threshold: large wordmark needs WordmarkWidth (87) + 8 chars of margin/border.
        // Note: the inner-width threshold (terminalWidth - 2) >= WordmarkWidth + 8
        // mirrors the rendering switch in BuildHeaderLines and avoids an
        // off-by-two mismatch at the boundary (terminalWidth ∈ {95, 96}).
        var headerLines = (terminalWidth - 2) >= WordmarkWidth + 8 ? 11 : 5;
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

    /// <summary>
    /// Returns the number of lines occupied by the launcher's combined
    /// header (wordmark) + URL bar region in the virtual content stream.
    /// </summary>
    internal static int ComputeHeaderPlusUrlBarLines(int terminalWidth)
    {
        // Mirror the BuildHeaderLines threshold: when inner width
        // (terminalWidth - 2) is at least WordmarkWidth + 8, the large 11-line
        // wordmark variant is shown; otherwise the 5-line narrow header.
        var headerLines = (terminalWidth - 2) >= WordmarkWidth + 8 ? 11 : 5;
        const int urlBarLines = 5;
        return headerLines + urlBarLines;
    }

    /// <summary>
    /// Returns the viewport height (in terminal lines) available for the
    /// launcher's scrolling content. The footer is pinned at the bottom of
    /// the screen and is excluded from this height.
    /// </summary>
    internal static int ComputeViewportHeight(int terminalHeight)
    {
        const int footerLines = 2;
        return Math.Max(1, terminalHeight - footerLines);
    }

    /// <summary>
    /// Returns the absolute terminal row (0-based) of the URL bar's input line,
    /// matching the layout produced by the header followed by the URL bar.
    /// The URL bar input line is the second of three box rows, after one
    /// leading blank line written by the URL-bar block.
    /// </summary>
    /// <remarks>
    /// Header line counts:
    /// <list type="bullet">
    ///   <item>Large wordmark (terminalWidth &gt;= WordmarkWidth + 8): top border + blank + 6 wordmark + subtitle + blank + bottom border = 11.</item>
    ///   <item>Narrow: top border + title + subtitle + blank + bottom border = 5.</item>
    /// </list>
    /// URL bar lines: blank + top border + content + bottom border + blank.
    /// The input line is therefore at headerLines + 2 (1 blank + 1 top border).
    /// Because the URL bar can only be focused when <c>pageScrollOffset == 0</c>
    /// (see <see cref="CommandHandlers.LauncherCommandHandler"/>), this row is
    /// always an absolute screen row when the URL bar is the active element.
    /// </remarks>
    internal static int ComputeUrlBarInputRow(int terminalWidth)
    {
        // Mirror the BuildHeaderLines switch on inner width (terminalWidth - 2).
        var headerLines = (terminalWidth - 2) >= WordmarkWidth + 8 ? 11 : 5;
        return headerLines + 2;
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

        // Accent bar column (1 char) — not highlighted, has its own muted color
        if (showAccent)
        {
            sb.Append(Selection.AccentBar(p));
        }
        else
        {
            sb.Append(' ');
        }

        // Every line of the selected cell gets sel-bg across the full contentWidth.
        // contentWidth is the cell width minus the accent bar column.
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
        else if (showAccent)
        {
            // Blank line within the content block (e.g. line 3 of a 5-line cell) —
            // fill full contentWidth with sel-bg so the highlight forms a
            // continuous rectangle across the selected cell.
            sb.Append($"{selBg}{new string(' ', contentWidth)}{Reset}");
        }
        else
        {
            // Top/bottom padding lines outside the accent range — no highlight,
            // matching LinkTreeRenderer's behavior for outside-content-block lines.
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
            name = "★ READING LIST";
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
            name = "★LIST";
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
            var combined = badge.Length > 0 ? $"{badge} {name}" : name;
            var truncCombined = RenderHelpers.TruncateText(combined, textWidth);
            sb.Append($"{selBg}{selFg}{Bold}{truncCombined}{Reset}");
            sb.Append($"{selBg}{new string(' ', Math.Max(0, contentWidth - truncCombined.Length))}{Reset}");
        }
        else if (lineIdx == 1)
        {
            var truncDomain = RenderHelpers.TruncateText(domain, textWidth);
            sb.Append($"{selBg}{p.SecondaryText.AnsiFg}{Dim} {truncDomain}{Reset}");
            sb.Append($"{selBg}{new string(' ', Math.Max(0, contentWidth - truncDomain.Length - 1))}{Reset}");
        }
        else
        {
            sb.Append($"{selBg}{new string(' ', contentWidth)}{Reset}");
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
            var truncDomain = RenderHelpers.TruncateText(domain, Math.Max(1, textWidth - 1));
            var pad = Math.Max(0, cellWidth - indent - truncDomain.Length - 1);
            return $"  {p.SecondaryText.AnsiFg}{Dim}{truncDomain}{Reset}{new string(' ', pad)}";
        }

        return new string(' ', cellWidth);
    }

    private static string FormatKbdHint(string key, string action, ThemePalette p)
    {
        return $"{p.SecondaryText.AnsiFg}[{Reset}{p.GetAccentFg().AnsiFg}{key}{Reset}{p.SecondaryText.AnsiFg}]{Reset} {p.SecondaryText.AnsiFg}{action}{Reset}";
    }

    /// <summary>
    /// Builds the wordmark / narrow-title header as a list of lines suitable
    /// for inclusion in the launcher's virtual content stream.
    /// </summary>
    private static List<string> BuildHeaderLines(int width, ThemePalette p)
    {
        var borderColor = p.GetDimFg().AnsiFg;
        var titleColor = p.HeaderTitleFg.AnsiFg;          // light pink (#ff87d7 ANSI 212)
        var titleColorDark = p.GetCelebrationFg().AnsiFg; // dark pink  (#ff5fd7 ANSI 206)
        var subtitle = "a better reading experience";
        var useLargeWordmark = width >= WordmarkWidth + 8;

        var boxOuter = useLargeWordmark
            ? Math.Min(width - 4, WordmarkWidth + 6)
            : Math.Clamp(width * 3 / 4, Math.Min(40, width - 4), 76);
        var leftMargin = Math.Max(0, (width - boxOuter - 2) / 2);
        var margin = new string(' ', leftMargin);

        var lines = new List<string>();

        string BoxLine(string content, int contentLen)
        {
            var pad = Math.Max(0, boxOuter - contentLen - 1);
            return $"{margin} {borderColor}│{Reset} {content}{new string(' ', pad)}{borderColor}│{Reset}";
        }

        string BlankBoxLine() =>
            $"{margin} {borderColor}│{Reset}{new string(' ', boxOuter)}{borderColor}│{Reset}";

        lines.Add($"{margin} {borderColor}╭{new string('─', boxOuter)}╮{Reset}");

        if (useLargeWordmark)
        {
            lines.Add(BlankBoxLine());
            for (var i = 0; i < Wordmark.Length; i++)
            {
                var rowColor = WordmarkUsesDark[i] ? titleColorDark : titleColor;
                lines.Add(BoxLine($"{rowColor}{Bold}{Wordmark[i]}{Reset}", Wordmark[i].Length));
            }
        }
        else
        {
            lines.Add(BoxLine($" {titleColor}{Bold}{"Wire Copy"}{Reset}", "Wire Copy".Length + 1));
        }

        lines.Add(BoxLine($" {p.SecondaryText.AnsiFg}{subtitle}{Reset}", subtitle.Length + 1));
        lines.Add(BlankBoxLine());
        lines.Add($"{margin} {borderColor}╰{new string('─', boxOuter)}╯{Reset}");

        return lines;
    }

    /// <summary>
    /// Builds the URL bar as a list of lines: blank, top border, content,
    /// bottom border, blank (5 lines total).
    /// </summary>
    private static List<string> BuildUrlBarLines(int width, bool isSelected, ThemePalette p)
    {
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

        return new List<string>
        {
            string.Empty,
            $"{new string(' ', pad)}{borderColor}╭{new string('─', barWidth - 2)}╮{Reset}",
            $"{new string(' ', pad)}{borderColor}│ {Reset}" +
            $"{textColor}{placeholder}{new string(' ', Math.Max(0, innerWidth - placeholder.Length))}{Reset}" +
            $"{borderColor} │{Reset}",
            $"{new string(' ', pad)}{borderColor}╰{new string('─', barWidth - 2)}╯{Reset}",
            string.Empty,
        };
    }

    /// <summary>
    /// Builds the full list of bookmark-region lines for a variant. The
    /// returned list contains <c>totalRows * cellHeight</c> lines for Grid
    /// and Compact, or <c>totalItems</c> lines for List (one item per row).
    /// </summary>
    private static List<string> BuildBookmarkLines(
        List<Bookmark> bookmarks,
        int selectedIndex,
        LauncherLayout layout,
        string variant,
        ThemePalette p)
    {
        var totalItems = bookmarks.Count + 1;
        var lines = new List<string>();

        if (variant == "List")
        {
            for (var row = 0; row < totalItems; row++)
            {
                lines.Add(BuildListLine(bookmarks, row, selectedIndex, layout.Width, p));
            }

            return lines;
        }

        var totalRows = (totalItems + layout.Columns - 1) / layout.Columns;

        for (var row = 0; row < totalRows; row++)
        {
            for (var line = 0; line < layout.CellHeight; line++)
            {
                if (variant == "Compact")
                {
                    lines.Add(BuildCompactRowLine(bookmarks, selectedIndex, layout, row, line, totalItems, p));
                }
                else
                {
                    lines.Add(BuildGridRowLine(bookmarks, selectedIndex, layout, row, line, totalItems, p));
                }
            }
        }

        return lines;
    }

    private static string BuildGridRowLine(
        List<Bookmark> bookmarks,
        int selectedIndex,
        LauncherLayout layout,
        int row,
        int line,
        int totalItems,
        ThemePalette p)
    {
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
            sb.Append($"{p.SecondaryText.AnsiFg}│{Reset}");
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

        return sb.ToString();
    }

    private static string BuildCompactRowLine(
        List<Bookmark> bookmarks,
        int selectedIndex,
        LauncherLayout layout,
        int row,
        int line,
        int totalItems,
        ThemePalette p)
    {
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
                sb.Append($"{p.SecondaryText.AnsiFg}│{Reset}");
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

        return sb.ToString();
    }

    /// <summary>
    /// Renders one of the three launcher variants using the combined
    /// virtual-content-stream model. Header (wordmark), URL bar, and bookmark
    /// rows are all built into a single list of lines; the lines from
    /// <c>scrollOffset</c> through <c>scrollOffset + viewportHeight</c> are
    /// then written to the screen. The footer is rendered separately at the
    /// bottom of the screen and is not part of the scrolling stream.
    /// </summary>
    private void RenderVariant(
        List<Bookmark> bookmarks,
        int selectedIndex,
        int scrollOffset,
        RenderOptions options,
        string variant)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, variant);
        var viewportHeight = ComputeViewportHeight(options.TerminalHeight);

        // Empty bookmarks: use the empty-state screen with the header pinned
        // at the top — there are no bookmarks to scroll past.
        if (bookmarks.Count == 0)
        {
            foreach (var headerLine in BuildHeaderLines(layout.Width, p))
            {
                _helpers.WriteLine(headerLine);
            }

            foreach (var urlBarLine in BuildUrlBarLines(layout.Width, selectedIndex == -1, p))
            {
                _helpers.WriteLine(urlBarLine);
            }

            RenderEmptyState(layout.Width, options.TerminalHeight, p);
            return;
        }

        // Build the full virtual content stream:
        //   [0 .. headerLines)              wordmark / title
        //   [headerLines .. +5)             URL bar
        //   [headerLines + 5 .. end)        bookmark rows
        var content = new List<string>();
        content.AddRange(BuildHeaderLines(layout.Width, p));
        content.AddRange(BuildUrlBarLines(layout.Width, selectedIndex == -1, p));
        content.AddRange(BuildBookmarkLines(bookmarks, selectedIndex, layout, variant, p));

        // Clamp scrollOffset so we don't scroll past the end of content.
        var maxScroll = Math.Max(0, content.Count - viewportHeight);
        var effectiveOffset = Math.Clamp(scrollOffset, 0, maxScroll);
        var endLine = Math.Min(content.Count, effectiveOffset + viewportHeight);

        for (var i = effectiveOffset; i < endLine; i++)
        {
            _helpers.WriteLine(content[i]);
        }
    }

    /// <summary>
    /// Writes the wordmark/header directly via <see cref="_helpers"/>. Kept
    /// as a private entry point so reflection-based layout tests
    /// (<see cref="LauncherUrlBarRowTests"/>) can invoke it. Production
    /// rendering uses <see cref="BuildHeaderLines"/> via <see cref="RenderVariant"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S1144:Unused private types or members should be removed",
        Justification = "Invoked by reflection from LauncherUrlBarRowTests.")]
    private void RenderHeader(int width, ThemePalette p)
    {
        foreach (var line in BuildHeaderLines(width, p))
        {
            _helpers.WriteLine(line);
        }
    }

    /// <summary>
    /// Writes the URL bar directly via <see cref="_helpers"/>. See
    /// <see cref="RenderHeader"/> for the rationale.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S1144:Unused private types or members should be removed",
        Justification = "Invoked by reflection from LauncherUrlBarRowTests.")]
    private void RenderUrlBar(int width, bool isSelected, ThemePalette p)
    {
        foreach (var line in BuildUrlBarLines(width, isSelected, p))
        {
            _helpers.WriteLine(line);
        }
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
}
