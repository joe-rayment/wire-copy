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

    // Reading List secondary-accent background (workspace-ul5z).
    // ANSI 23 (#005f5f, dark cyan teal) — chosen as a dimmed counterpart to
    // AccentFg (ANSI 51 cyan) which the design system reserves for interactive
    // accents. Reads as "secondary accent": present and identifiable, but
    // visually quieter than the selected cell's brightened green border + bg.
    // Source: docs/design-system.md (cyan accent role) — see workspace-ul5z.
    private const string ReadingListBg = "\x1b[48;5;23m";
    private const string ResetBg = "\x1b[49m";

    private const int WordmarkWidth = 87;

    // Grid (boxed) cell layout (workspace-wxht):
    //   line 0: top border    ╭───╮
    //   line 1: title          │ NAME            [N] │
    //   line 2: url            │ domain.example      │
    //   line 3: bottom border  ╰───╯
    //   line 4: blank          (inter-row gutter)
    private const int GridCellBoxHeight = 4;
    private const int GridRowStride = GridCellBoxHeight + 1;

    // 6-row ASCII-art wordmark for "WIRE COPY" (hand-crafted block letters).
    // Two-tone pink: outer rows (1,2,5,6) in HeaderTitleFg (#ff87d7 ANSI 212),
    // inner rows (3,4) in CelebrationFg (#ff5fd7 ANSI 206) for vertical stripe.
    // Width: 87 columns. Falls back to single-line title when terminal is narrower.
    private static readonly string[] Wordmark =
    [
        "      ██╗    ██╗ ██╗ ██████╗  ███████╗      ██████╗  ██████╗  ██████╗  ██╗   ██╗       ",
        "      ██║    ██║ ██║ ██╔══██╗ ██╔════╝     ██╔════╝ ██╔═══██╗ ██╔══██╗ ╚██╗ ██╔╝       ",
        "      ██║ █╗ ██║ ██║ ██████╔╝ █████╗       ██║      ██║   ██║ ██████╔╝  ╚████╔╝        ",
        "      ██║███╗██║ ██║ ██╔══██╗ ██╔══╝       ██║      ██║   ██║ ██╔═══╝    ╚██╔╝         ",
        "      ╚███╔███╔╝ ██║ ██║  ██║ ███████╗     ╚██████╗ ╚██████╔╝ ██║         ██║          ",
        "       ╚══╝╚══╝  ╚═╝ ╚═╝  ╚═╝ ╚══════╝      ╚═════╝  ╚═════╝  ╚═╝         ╚═╝          ",
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
    /// The primary action key (`Enter`) is rendered in the accent colour;
    /// secondary keys (`o`, `a`, `d`, `?`) render in the muted secondary tone
    /// so the eye lands on Enter first. Version is no longer shown here —
    /// it lives under the tagline in the header card (workspace-m8x2).
    /// </summary>
    public void RenderFooter(int width)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);

        var hints = FormatPrimaryKbdHint("Enter", "open", p) + "  " +
                    FormatMutedKbdHint("o", "go to url", p) + "  " +
                    FormatMutedKbdHint("a", "add", p) + "  " +
                    FormatMutedKbdHint("d", "delete", p) + "  " +
                    FormatMutedKbdHint("?", "help", p);

        _ = width;
        _helpers.WriteLine($" {hints}");
    }

    /// <summary>
    /// Computes shared layout parameters from terminal dimensions.
    /// Used by both the renderer and <see cref="CommandHandlers.LauncherCommandHandler"/>.
    /// </summary>
    internal static LauncherLayout ComputeLayout(int terminalWidth, int terminalHeight)
    {
        return ComputeLayout(terminalWidth, terminalHeight, "Grid", showSetupHint: false);
    }

    internal static LauncherLayout ComputeLayout(int terminalWidth, int terminalHeight, string variant)
    {
        return ComputeLayout(terminalWidth, terminalHeight, variant, showSetupHint: false);
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
    internal static LauncherLayout ComputeLayout(int terminalWidth, int terminalHeight, string variant, bool showSetupHint)
    {
        // Large wordmark: border + pad + 6 art rows + subtitle + pad + border = 11
        // Narrow: border + title + subtitle + pad + border = 5
        // Threshold: large wordmark needs WordmarkWidth (87) + 8 chars of margin/border.
        // Note: the inner-width threshold (terminalWidth - 2) >= WordmarkWidth + 8
        // mirrors the rendering switch in BuildHeaderLines and avoids an
        // off-by-two mismatch at the boundary (terminalWidth ∈ {95, 96}).
        // showSetupHint affects only header *content* (the trailing blank inside
        // the box becomes a hint line) — total header line count is unchanged.
        var headerLines = (terminalWidth - 2) >= WordmarkWidth + 8 ? 11 : 5;
        const int urlBarLines = 5;
        const int footerLines = 2;
        _ = showSetupHint;

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
                columns = width >= columnThreshold ? 2 : 1;

                // Boxed grid cell: 4 visible box lines (top border + title + url
                // + bottom border) plus 1 trailing blank line for the
                // inter-row vertical gutter (workspace-wxht). The scroll math
                // and viewport sizing treat the whole 5-line block as the
                // logical row height so a row never partially scrolls in.
                cellHeight = GridRowStride;
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
        return ComputeHeaderPlusUrlBarLines(terminalWidth, showSetupHint: false);
    }

    /// <summary>
    /// Returns the line offset (in the virtual content stream) of the first
    /// bookmark row. Setup hint no longer adds rows of its own — it lives
    /// inside the header card (workspace-ayt8) — so the result is identical
    /// for both flag values. The overload is kept for source compatibility.
    /// </summary>
    internal static int ComputeHeaderPlusUrlBarLines(int terminalWidth, bool showSetupHint)
    {
        // Mirror the BuildHeaderLines threshold: when inner width
        // (terminalWidth - 2) is at least WordmarkWidth + 8, the large 11-line
        // wordmark variant is shown; otherwise the 5-line narrow header.
        // showSetupHint affects only header *content*, not its line count.
        _ = showSetupHint;
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

    /// <summary>
    /// Builds one line of a boxed grid cell (workspace-wxht).
    /// Cell layout (4 lines): top border, title (name + right-aligned [N] badge),
    /// url, bottom border. The trailing inter-row gutter is rendered by the
    /// row-level builder, not this helper.
    /// </summary>
    private static string BuildBoxedGridCell(
        List<Bookmark> bookmarks,
        int itemIdx,
        int selectedIndex,
        int cellWidth,
        int lineIdx,
        ThemePalette p,
        int? readingListCount = null)
    {
        var totalItems = bookmarks.Count + 1;
        if (itemIdx >= totalItems)
        {
            return new string(' ', cellWidth);
        }

        // Slot layout (workspace-ul5z): with bookmarks present, virtual
        // index 1 is the Reading List; bookmarks shift one slot later.
        //   virtual 0 → bookmark[0]
        //   virtual 1 → Reading List (secondary-accent fill)
        //   virtual N (≥ 2) → bookmark[N - 1]
        var isReadingList = itemIdx == 1 && bookmarks.Count > 0;
        var isSelected = itemIdx == selectedIndex;

        string name;
        string domain;

        if (isReadingList)
        {
            name = "READING LIST";
            domain = ReadingListSubtitle(readingListCount);
        }
        else
        {
            var bookmarkIdx = itemIdx == 0 ? 0 : itemIdx - 1;
            var bookmark = bookmarks[bookmarkIdx];
            name = bookmark.Name.ToUpperInvariant();
            domain = ExtractDomain(bookmark.Url);
        }

        // Digit badge `[N]` for slots 0..8 (digits 1-9). Reading List keeps a
        // numeric badge consistent with its slot (`[2]`) so the digit-jump
        // contract stays uniform across all addressable slots. Items past
        // slot 8 render no badge — same contract as before.
        string badge;
        if (itemIdx < 9)
        {
            badge = $"[{itemIdx + 1}]";
        }
        else
        {
            badge = string.Empty;
        }

        // Borders: dim grey by default; brighten to PrimaryText when selected.
        // Reading List uses the same border style — the secondary-accent
        // background fill is what differentiates it, not the border colour.
        var borderColor = isSelected
            ? $"{p.PrimaryText.AnsiFg}"
            : $"{p.SecondaryText.AnsiFg}{Dim}";
        var titleSegment = $"{Bold}{p.PrimaryText.AnsiFg}";
        var badgeColor = $"{p.SecondaryText.AnsiFg}{Dim}";
        var domainColor = p.SecondaryText.AnsiFg;

        // Background fill for the Reading List cell. Applied across ALL four
        // box rows so the whole tile shows the fill, not just inner content.
        // Reset before any inter-cell content (the row builder is responsible
        // for not letting the fill bleed into the gutter).
        var bg = isReadingList ? ReadingListBg : string.Empty;
        var bgReset = isReadingList ? ResetBg : string.Empty;

        // Inner content width = cellWidth - 2 borders - 2 single-cell pads.
        var inner = Math.Max(1, cellWidth - 4);

        switch (lineIdx)
        {
            case 0:
                // ╭───────────╮
                return $"{bg}{borderColor}╭{new string('─', cellWidth - 2)}╮{Reset}{bgReset}";

            case 1:
            {
                // │ NAME .................. [N] │  (badge right-aligned)
                // Reserve 5 cells for the badge zone (" [N]") so the title never
                // collides with it. Items without a badge get the full inner width.
                var badgeZone = badge.Length > 0 ? badge.Length + 1 : 0; // 1-cell pre-pad
                var titleMax = Math.Max(1, inner - badgeZone);
                var truncName = RenderHelpers.TruncateText(name, titleMax);
                var nameDisplayLen = truncName.Length;
                var gap = Math.Max(0, inner - nameDisplayLen - badgeZone);

                var renderedTitle = $"{bg}{titleSegment}{truncName}{Reset}{bg}";

                if (badge.Length > 0)
                {
                    return $"{bg}{borderColor}│{Reset}{bg} " +
                           $"{renderedTitle}" +
                           $"{new string(' ', gap)}" +
                           $"{badgeColor}{badge}{Reset}{bg}" +
                           $" {borderColor}│{Reset}{bgReset}";
                }

                // No badge — pad the full width.
                var padNoBadge = Math.Max(0, inner - nameDisplayLen);
                return $"{bg}{borderColor}│{Reset}{bg} " +
                       $"{renderedTitle}" +
                       $"{new string(' ', padNoBadge)}" +
                       $" {borderColor}│{Reset}{bgReset}";
            }

            case 2:
            {
                // │ domain.example                │
                var truncDomain = RenderHelpers.TruncateText(domain, inner);
                var pad = Math.Max(0, inner - truncDomain.Length);
                return $"{bg}{borderColor}│{Reset}{bg} " +
                       $"{domainColor}{truncDomain}{Reset}{bg}" +
                       $"{new string(' ', pad)}" +
                       $" {borderColor}│{Reset}{bgReset}";
            }

            case 3:
                // ╰───────────╯
                return $"{bg}{borderColor}╰{new string('─', cellWidth - 2)}╯{Reset}{bgReset}";

            default:
                // Defensive — caller renders the inter-row gap blank line itself,
                // not via this helper. Anything else is a blank cell-width fill.
                return new string(' ', cellWidth);
        }
    }

    /// <summary>
    /// Builds a single line for the List layout variant.
    /// </summary>
    private static string BuildListLine(
        List<Bookmark> bookmarks,
        int itemIdx,
        int selectedIndex,
        int width,
        ThemePalette p,
        int? readingListCount = null)
    {
        var totalItems = bookmarks.Count + 1;
        if (itemIdx >= totalItems)
        {
            return new string(' ', width);
        }

        // Slot layout (workspace-ul5z): Reading List sits at virtual index 1.
        var isReadingList = itemIdx == 1 && bookmarks.Count > 0;
        var isSelected = itemIdx == selectedIndex;

        string name;
        string domain;

        if (isReadingList)
        {
            name = "★ READING LIST";
            domain = ReadingListSubtitle(readingListCount);
        }
        else
        {
            var bookmarkIdx = itemIdx == 0 ? 0 : itemIdx - 1;
            var bookmark = bookmarks[bookmarkIdx];
            name = bookmark.Name.ToUpperInvariant();
            domain = ExtractDomain(bookmark.Url);
        }

        string badge;
        if (itemIdx < 9)
        {
            badge = $"[{itemIdx + 1}]";
        }
        else
        {
            badge = "   ";
        }

        // Right-align badge for parity with Grid (workspace-wxht).
        // Layout: `▌ NAME .................. domain  [N] `
        const int badgeWidth = 3;
        const int gapMin = 2;
        var domainMaxWidth = Math.Min(domain.Length, (width - badgeWidth - gapMin - 3) / 3);
        var truncDomain = RenderHelpers.TruncateText(domain, domainMaxWidth);
        var nameMaxWidth = Math.Max(1, width - badgeWidth - 2 - gapMin - truncDomain.Length - 1);
        var truncName = RenderHelpers.TruncateText(name, nameMaxWidth);
        var gap = Math.Max(0, width - 2 - badgeWidth - truncName.Length - truncDomain.Length - 1);
        var badgeColor = $"{p.SecondaryText.AnsiFg}{Dim}";

        if (isSelected)
        {
            var selFg = p.SelectedItemFg.AnsiFg;
            var selBg = p.SelectedItemBg.AnsiBg;
            var sb = new System.Text.StringBuilder();
            sb.Append(Selection.AccentBar(p));
            sb.Append($"{selBg}{Bold}{selFg}{truncName}{Reset}");
            sb.Append($"{selBg}{new string(' ', gap)}");
            sb.Append($"{p.SecondaryText.AnsiFg}{selBg}{truncDomain}{Reset}");
            sb.Append($"{selBg}  ");
            sb.Append($"{badgeColor}{badge}{Reset}");
            sb.Append($"{selBg} {Reset}");
            return sb.ToString();
        }

        var sb2 = new System.Text.StringBuilder();
        sb2.Append(' ');
        sb2.Append($"{Bold}{p.PrimaryText.AnsiFg}{truncName}{Reset}");
        sb2.Append(new string(' ', gap));
        sb2.Append($"{p.SecondaryText.AnsiFg}{truncDomain}{Reset}  ");
        sb2.Append($"{badgeColor}{badge}{Reset} ");
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
        ThemePalette p,
        int? readingListCount = null)
    {
        var totalItems = bookmarks.Count + 1;
        if (itemIdx >= totalItems)
        {
            return new string(' ', cellWidth);
        }

        // Slot layout (workspace-ul5z): Reading List sits at virtual index 1.
        var isReadingList = itemIdx == 1 && bookmarks.Count > 0;
        var isSelected = itemIdx == selectedIndex;

        string name;
        string domain;

        if (isReadingList)
        {
            name = "★LIST";
            domain = ReadingListSubtitle(readingListCount);
        }
        else
        {
            var bookmarkIdx = itemIdx == 0 ? 0 : itemIdx - 1;
            var bookmark = bookmarks[bookmarkIdx];
            name = bookmark.Name.ToUpperInvariant();
            domain = ExtractDomain(bookmark.Url);
        }

        string badge;
        if (itemIdx < 9)
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
        var badgeColor = $"{p.SecondaryText.AnsiFg}{Dim}";

        if (lineIdx == 0)
        {
            // Right-align badge (workspace-wxht).
            var badgeZone = badge.Length > 0 ? badge.Length + 1 : 0;
            var nameMax = Math.Max(1, textWidth - badgeZone);
            var truncName = RenderHelpers.TruncateText(name, nameMax);
            var pad = Math.Max(0, contentWidth - truncName.Length - badgeZone);
            sb.Append($"{selBg}{Bold}{selFg}{truncName}{Reset}");
            sb.Append($"{selBg}{new string(' ', pad)}{Reset}");
            if (badge.Length > 0)
            {
                sb.Append($"{selBg}{badgeColor}{badge}{Reset}{selBg} {Reset}");
            }
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
            // Right-align badge for parity with Grid (workspace-wxht).
            var badgeZone = badge.Length > 0 ? badge.Length + 1 : 0;
            var nameMax = Math.Max(1, textWidth - badgeZone);
            var truncName = RenderHelpers.TruncateText(name, nameMax);
            var nameLen = truncName.Length;
            var pad = Math.Max(0, cellWidth - indent - nameLen - badgeZone);
            var badgeColor = $"{p.SecondaryText.AnsiFg}{Dim}";
            var titleSegment = $"{Bold}{p.PrimaryText.AnsiFg}{truncName}{Reset}";

            if (badge.Length > 0)
            {
                return $" {titleSegment}{new string(' ', pad)}{badgeColor}{badge}{Reset} ";
            }

            return $" {titleSegment}{new string(' ', pad)} ";
        }

        if (lineIdx == 1)
        {
            var truncDomain = RenderHelpers.TruncateText(domain, Math.Max(1, textWidth - 1));
            var pad = Math.Max(0, cellWidth - indent - truncDomain.Length - 1);
            return $"  {p.SecondaryText.AnsiFg}{Dim}{truncDomain}{Reset}{new string(' ', pad)}";
        }

        return new string(' ', cellWidth);
    }

    /// <summary>
    /// Resolves the launcher version string for the tagline (workspace-m8x2).
    /// Returns the major.minor pair of the assembly version (e.g. "1.0") so
    /// the surface stays tidy; falls back to "1.0" if no version attribute is
    /// present on the renderer assembly.
    /// </summary>
    private static string ResolveLauncherVersion()
    {
        var asm = typeof(LauncherRenderer).Assembly;
        return asm.GetName().Version?.ToString(2) ?? "1.0";
    }

    /// <summary>
    /// Renders a primary action's `[key]` glyph in the accent colour so the
    /// eye lands on it first; the action label keeps the muted secondary tone.
    /// Used for `[Enter]` (workspace-m8x2).
    /// </summary>
    private static string FormatPrimaryKbdHint(string key, string action, ThemePalette p)
    {
        var accent = p.GetAccentFg().AnsiFg;
        return $"{accent}[{key}]{Reset} {p.SecondaryText.AnsiFg}{action}{Reset}";
    }

    /// <summary>
    /// Renders a non-primary action's `[key]` glyph in muted dim so the
    /// primary key glyph stands out (workspace-m8x2). Action label
    /// matches the existing dim tone.
    /// </summary>
    private static string FormatMutedKbdHint(string key, string action, ThemePalette p)
    {
        var muted = p.SecondaryText.AnsiFg;
        return $"{muted}{Dim}[{key}]{Reset} {muted}{action}{Reset}";
    }

    /// <summary>
    /// Builds the wordmark / narrow-title header as a list of lines suitable
    /// for inclusion in the launcher's virtual content stream.
    /// When <paramref name="showSetupHint"/> is true, the trailing blank line
    /// inside the box is replaced with a centred "Set up API keys" hint in the
    /// accent colour (workspace-ayt8). Net header height is unchanged.
    /// </summary>
#pragma warning disable SA1202 // exposed internal for LauncherTaglineAlignmentTests; private siblings precede.
    internal static List<string> BuildHeaderLines(int width, ThemePalette p, bool showSetupHint)
#pragma warning restore SA1202
    {
        var borderColor = p.GetDimFg().AnsiFg;
        var titleColor = p.HeaderTitleFg.AnsiFg;          // light pink (#ff87d7 ANSI 212)
        var titleColorDark = p.GetCelebrationFg().AnsiFg; // dark pink  (#ff5fd7 ANSI 206)
        var subtitle = "All copy, no clutter.";
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

        string SetupHintBoxLine()
        {
            const string fullLabel = "→ Set up API keys to enable AI features";
            const string suffix = " · press S";
            var accent = p.GetAccentFg().AnsiFg;
            var muted = p.SecondaryText.AnsiFg;

            // Inner content width (between the box pipes plus a one-cell pad on each side).
            var inner = Math.Max(0, boxOuter - 2);

            // Truncation order: drop suffix first, then truncate the label.
            var label = fullLabel;
            var includeSuffix = suffix.Length + label.Length <= inner;
            if (!includeSuffix && label.Length > inner)
            {
                label = label[..Math.Max(0, inner)];
            }

            var visible = includeSuffix ? label.Length + suffix.Length : label.Length;
            var leftPad = Math.Max(0, (inner - visible) / 2);
            var rightPad = Math.Max(0, inner - leftPad - visible);

            var rendered = includeSuffix
                ? $"{new string(' ', leftPad)}{accent}{label}{Reset}{muted}{suffix}{Reset}{new string(' ', rightPad)}"
                : $"{new string(' ', leftPad)}{accent}{label}{Reset}{new string(' ', rightPad)}";

            return $"{margin} {borderColor}│{Reset} {rendered} {borderColor}│{Reset}";
        }

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

        // Align tagline's left edge with the W glyph above (workspace-usr3).
        // Large wordmark[0] starts with 6 spaces before "██╗" (the W); the
        // narrow fallback "Wire Copy" is rendered with a single space pad.
        // Append `  ·  v{version}` in muted dim so the tagline remains the
        // focal point and the version metadata sits with identity, not next
        // to the keybindings (workspace-m8x2).
        var taglinePad = useLargeWordmark ? "      " : " ";
        var version = ResolveLauncherVersion();
        var versionSuffix = $"  ·  v{version}";
        var versionStyled = $"{p.GetDimFg().AnsiFg}{Dim}{versionSuffix}{Reset}";
        lines.Add(BoxLine(
            $"{taglinePad}{p.SecondaryText.AnsiFg}{subtitle}{Reset}{versionStyled}",
            subtitle.Length + taglinePad.Length + versionSuffix.Length));

        // Trailing blank-before-bottom-border becomes the setup hint when first-run.
        // Net header height (11 large / 5 narrow) is unchanged.
        lines.Add(showSetupHint ? SetupHintBoxLine() : BlankBoxLine());
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
        ThemePalette p,
        int? readingListCount = null)
    {
        var totalItems = bookmarks.Count + 1;
        var lines = new List<string>();

        if (variant == "List")
        {
            for (var row = 0; row < totalItems; row++)
            {
                lines.Add(BuildListLine(bookmarks, row, selectedIndex, layout.Width, p, readingListCount));
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
                    lines.Add(BuildCompactRowLine(bookmarks, selectedIndex, layout, row, line, totalItems, p, readingListCount));
                }
                else
                {
                    lines.Add(BuildGridRowLine(bookmarks, selectedIndex, layout, row, line, totalItems, p, readingListCount));
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Subtitle text for the launcher's Reading List tile (workspace-fbcn).
    /// Shows "{N} saved articles" (singular when 1) when populated; falls back
    /// to the empty-state copy when zero or count unknown (count is only
    /// populated on launcher view via <see cref="RenderOptions.ReadingListItemCount"/>).
    /// </summary>
    private static string ReadingListSubtitle(int? count)
    {
        if (count is null or 0)
        {
            return "nothing saved yet — press c on any link to add";
        }

        return count == 1 ? "1 saved article" : $"{count} saved articles";
    }

    private static string BuildGridRowLine(
        List<Bookmark> bookmarks,
        int selectedIndex,
        LauncherLayout layout,
        int row,
        int line,
        int totalItems,
        ThemePalette p,
        int? readingListCount = null)
    {
        // The Grid path uses a 5-line stride per row (workspace-wxht):
        //   line 0..3 — boxed cell content
        //   line 4    — blank inter-row gutter
        // Adjacent boxes are separated by a single space gutter (their
        // borders form the visual separator); the explicit `│` divider
        // from the previous design is intentionally omitted.
        if (line >= GridCellBoxHeight)
        {
            return new string(' ', layout.Width);
        }

        var sb = new System.Text.StringBuilder();
        var leftIdx = row * layout.Columns;
        sb.Append(BuildBoxedGridCell(
            bookmarks,
            leftIdx,
            selectedIndex,
            layout.CellWidth,
            line,
            p,
            readingListCount));

        if (layout.Columns == 2)
        {
            // 1-cell horizontal gutter between adjacent boxes.
            sb.Append(' ');
            var rightIdx = leftIdx + 1;
            var rightWidth = layout.Width - layout.CellWidth - 1;
            if (rightIdx < totalItems)
            {
                sb.Append(BuildBoxedGridCell(
                    bookmarks,
                    rightIdx,
                    selectedIndex,
                    rightWidth,
                    line,
                    p,
                    readingListCount));
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
        ThemePalette p,
        int? readingListCount = null)
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
                    bookmarks, itemIdx, selectedIndex, cellW, line, p, readingListCount));
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
        var showSetupHint = options.ShowSetupHint;
        var layout = ComputeLayout(options.TerminalWidth, options.TerminalHeight, variant, showSetupHint);
        var viewportHeight = ComputeViewportHeight(options.TerminalHeight);

        // Empty bookmarks: use the empty-state screen with the header pinned
        // at the top — there are no bookmarks to scroll past.
        if (bookmarks.Count == 0)
        {
            foreach (var headerLine in BuildHeaderLines(layout.Width, p, showSetupHint))
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
        //   [0 .. headerLines)              wordmark / title (setup hint inside header card when ShowSetupHint)
        //   [headerLines .. +5)             URL bar
        //   [headerLines + 5 .. end)        bookmark rows
        var content = new List<string>();
        content.AddRange(BuildHeaderLines(layout.Width, p, showSetupHint));
        content.AddRange(BuildUrlBarLines(layout.Width, selectedIndex == -1, p));
        content.AddRange(BuildBookmarkLines(bookmarks, selectedIndex, layout, variant, p, options.ReadingListItemCount));

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
        foreach (var line in BuildHeaderLines(width, p, showSetupHint: false))
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
