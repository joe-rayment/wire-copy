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

    // Reading List glyph: cyan ★ prefix marks the slot as a different kind
    // of tile (a saved-articles container, not a saved URL) without leaving
    // the design palette. The accent colour comes from ThemePalette.AccentFg
    // at render time so each theme maps it to its own accent hue.
    private const string ReadingListGlyph = "★";

    private const int WordmarkWidth = 87;

    // Grid card cell layout (workspace-bs93/stby, rebalanced by the Launcher.dc.html
    // design import, workspace-e86u) — mirrors LinkTreeRenderer's card vocabulary
    // so launcher and link-list tiles read as one product. The title + subtitle
    // block is vertically CENTRED in the cell's content area (the design's
    // justify-content:center), so a taller cell pads symmetrically instead of
    // pinning the copy to the top with a void beneath. At the 5-line floor the
    // layout is identical to the classic card:
    //   line 0: blank padding    (centring pad above the title)
    //   line 1: title row        " NAME                [N]"  ← ▌ replaces the leading
    //                                                          space when selected
    //   line 2: subtitle row     " domain.example"           ← same ▌ rule
    //   line 3: interior padding (centring pad below the subtitle)
    //   line 4: separator rule   "────────────────"          ← structural chrome; the
    //                                                          bottom grid row draws NO
    //                                                          rule — the footer's top
    //                                                          rule caps the grid instead
    // No box-drawing characters around the card. Since workspace-21uy the cell
    // height GROWS to fill the screen at ~4 rows (ResponsiveGrid.CellHeightFor) —
    // the centring pads absorb the extra lines — and this constant is the FLOOR
    // so short windows keep the classic 5-line card.
    private const int GridCardHeight = 5;

    // URL bar block height (workspace-0rde): blank + top border + content
    // + bottom border = 4 lines. The trailing blank that previously padded
    // the bar from below was removed; the first bookmark row's own top
    // border now sits directly under the URL bar's bottom border with
    // no extra gutter.
    private const int UrlBarLines = 4;

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
    /// Renders the launcher-specific footer per Launcher.dc.html
    /// (workspace-e86u): a dim top rule that caps the (rule-less) bottom grid
    /// row, then `[key]:action` hints — keys in the interactive accent, the
    /// bracket/label chrome muted — with a right-aligned dim bookmark count.
    /// Version is not shown here — it lives under the tagline in the header
    /// card (workspace-m8x2).
    /// </summary>
    public void RenderFooter(int width, int bookmarkCount, string? scheduledRunBadge = null, string? statusMessage = null)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var contentWidth = Math.Max(1, width - 2);

        // workspace-xx61 (511u) + workspace-ej1i: the launcher has no status bar,
        // so a status message set by a launcher action (e.g. "Couldn't move
        // bookmark", "Already at top") was never shown anywhere — surface it here
        // or failures stay silent. The footer budget is 2 lines because
        // PositionAtBottom leaves 2 — a transient status (or a scheduled-run
        // badge, workspace-frpl.13 B11) takes the rule's slot and the rule
        // returns next render; a warning outranks chrome.
        if (!string.IsNullOrEmpty(statusMessage))
        {
            _helpers.WriteLine($" {p.GetWarningFg().AnsiFg}{statusMessage}{Reset}");
        }
        else if (!string.IsNullOrEmpty(scheduledRunBadge))
        {
            _helpers.WriteLine($" {p.GetWarningFg().AnsiFg}{scheduledRunBadge}{Reset}");
        }
        else
        {
            _helpers.WriteLine($"{p.GetDimFg().AnsiFg}{new string('─', contentWidth)}{Reset}");
        }

        // workspace-g801: surface the launcher's signature 1-9 quick-jump (the tile
        // badges already advertise it); the destructive 'd delete' moves to ?-help
        // rather than sitting on the footer of the very first screen forever.
        // workspace-ej1i.3: "?" is labelled "all keys" so it reads as the gateway
        // to the full binding list.
        var hintPairs = new (string Key, string Action)[]
        {
            ("Enter", "open"), ("1-9", "jump"), ("o", "go to url"), ("a", "add"), ("?", "all keys"),
        };
        var hints = string.Join("  ", hintPairs.Select(h => FormatKbdHint(h.Key, h.Action, p)));
        var hintsVisibleLength = hintPairs.Sum(h => h.Key.Length + h.Action.Length + 3) +
                                 ((hintPairs.Length - 1) * 2);

        // Right-aligned dim count (Launcher.dc.html "6 bookmarks") — dropped
        // when the window is too narrow to keep at least one space of gap.
        var count = bookmarkCount == 1 ? "1 bookmark" : $"{bookmarkCount} bookmarks";
        var gap = contentWidth - 1 - hintsVisibleLength - count.Length;
        if (gap >= 1)
        {
            _helpers.WriteLine($" {hints}{new string(' ', gap)}{p.GetDimFg().AnsiFg}{count}{Reset}");
        }
        else
        {
            _helpers.WriteLine($" {hints}");
        }
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
        // Header height (workspace-0rde, after vertical-compression):
        //   Large wordmark + setup hint: border + blank + 6 art + subtitle + hint + border = 11
        //   Large wordmark, no hint:     border + blank + 6 art + subtitle + border         = 10
        //   Narrow + setup hint:         border + title + subtitle + hint + border          = 5
        //   Narrow, no hint:             border + title + subtitle + border                 = 4
        // Threshold: large wordmark needs WordmarkWidth (87) + 8 chars of margin/border.
        // Note: the inner-width threshold (terminalWidth - 2) >= WordmarkWidth + 8
        // mirrors the rendering switch in BuildHeaderLines and avoids an
        // off-by-two mismatch at the boundary (terminalWidth ∈ {95, 96}).
        var headerLines = HeaderLineCount(terminalWidth, showSetupHint);
        const int urlBarLines = UrlBarLines;
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
                columns = ResponsiveGrid.ColumnsFor(width);
                cellHeight = 3;
                break;
            }

            default: // Grid
            {
                // Fixed 2-column grid (workspace-21uy): cells split the width
                // at every window size — narrower when the sidecar docks, wider
                // fullscreen — never a different column count.
                columns = ResponsiveGrid.ColumnsFor(width);

                // Card cell: blank pad + title + subtitle + padding + separator
                // rule. A tile is ~a quarter of the FULL screen tall
                // (ResponsiveGrid.CellHeightFor, workspace-21uy/1ogw — the
                // header/URL bar reduce how many are visible, not how big they
                // are), flooring at the classic 5-line stride
                // (workspace-bs93/stby) so short windows scroll instead of
                // shrinking. Shares the formula with the link-list card so the
                // two views feel like the same product. Scroll math treats the
                // block as the logical row height so a row never partially
                // scrolls in.
                cellHeight = ResponsiveGrid.CellHeightFor(terminalHeight, GridCardHeight);
                break;
            }
        }

        var visibleRows = Math.Max(1, availableHeight / cellHeight);
        var cellWidth = ResponsiveGrid.CellWidthFor(width, columns);

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
    /// bookmark row. After workspace-0rde, the setup hint adds exactly one
    /// row to the header card (the trailing blank-before-bottom-border is now
    /// elided when the hint is absent), so callers must pass the active
    /// flag to get the correct offset.
    /// </summary>
    internal static int ComputeHeaderPlusUrlBarLines(int terminalWidth, bool showSetupHint)
    {
        return HeaderLineCount(terminalWidth, showSetupHint) + UrlBarLines;
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
    /// Header line counts (workspace-0rde):
    /// <list type="bullet">
    ///   <item>Large wordmark (terminalWidth &gt;= WordmarkWidth + 8): top border + blank + 6 wordmark + subtitle + bottom border = 10 (or 11 with setup hint).</item>
    ///   <item>Narrow: top border + title + subtitle + bottom border = 4 (or 5 with setup hint).</item>
    /// </list>
    /// URL bar lines: blank + top border + content + bottom border (4 lines after compression).
    /// The input line is therefore at headerLines + 2 (1 blank + 1 top border).
    /// Because the URL bar can only be focused when <c>pageScrollOffset == 0</c>
    /// (see <see cref="CommandHandlers.LauncherCommandHandler"/>), this row is
    /// always an absolute screen row when the URL bar is the active element.
    /// </remarks>
    internal static int ComputeUrlBarInputRow(int terminalWidth)
    {
        return ComputeUrlBarInputRow(terminalWidth, showSetupHint: false);
    }

    /// <summary>
    /// Returns the absolute terminal row (0-based) of the URL bar's input
    /// line for the given header variant. The setup-hint flag adds one row
    /// to the header card (workspace-0rde).
    /// </summary>
    internal static int ComputeUrlBarInputRow(int terminalWidth, bool showSetupHint)
    {
        // Mirror the BuildHeaderLines switch on inner width (terminalWidth - 2).
        return HeaderLineCount(terminalWidth, showSetupHint) + 2;
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
    /// Returns the header card height for the given terminal width and
    /// setup-hint state. Centralised so <see cref="ComputeLayout"/>,
    /// <see cref="ComputeHeaderPlusUrlBarLines"/>, and
    /// <see cref="ComputeUrlBarInputRow"/> always agree on the row math
    /// (workspace-0rde).
    /// </summary>
    private static int HeaderLineCount(int terminalWidth, bool showSetupHint)
    {
        var useLargeWordmark = (terminalWidth - 2) >= WordmarkWidth + 8;
        if (useLargeWordmark)
        {
            return showSetupHint ? 11 : 10;
        }

        return showSetupHint ? 5 : 4;
    }

    /// <summary>
    /// Builds one line of a card-style launcher cell (workspace-bs93, rebalanced
    /// per the Launcher.dc.html design import in workspace-e86u).
    /// Mirrors <see cref="LinkTreeRenderer.BuildCardLine"/> so launcher tiles
    /// and link-list tiles share visual vocabulary.
    /// The title + subtitle pair is vertically centred in the content area
    /// (every line except the trailing separator slot); remaining lines are
    /// centring pads that carry the selection fill when selected.
    /// Reading List renders like a bookmark card — green title with a trailing
    /// ★ in the metadata green — per the design; only the SELECTED card shows
    /// the muted ▌ rail.
    /// <paramref name="drawSeparator"/> is false on the grid's bottom row: the
    /// design caps the grid with the footer's top rule instead of a per-cell
    /// rule, so the last line renders as (selection-filled) padding there.
    /// </summary>
    private static string BuildCardCellLine(
        List<Bookmark> bookmarks,
        int itemIdx,
        int selectedIndex,
        int cellWidth,
        int lineIdx,
        int cellHeight,
        ThemePalette p,
        int? readingListCount = null,
        bool drawSeparator = true)
    {
        var totalItems = bookmarks.Count + 1;
        if (itemIdx >= totalItems)
        {
            return new string(' ', cellWidth);
        }

        // Slot layout (workspace-ul5z): with bookmarks present, virtual
        // index 1 is the Reading List; bookmarks shift one slot later.
        //   virtual 0 → bookmark[0]
        //   virtual 1 → Reading List (cyan accent bar + ★ glyph)
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
        var badge = itemIdx < 9 ? $"[{itemIdx + 1}]" : string.Empty;

        var accentFg = p.GetAccentFg().AnsiFg;
        var selBg = p.SelectedItemBg.AnsiBg;
        var selFg = p.SelectedItemFg.AnsiFg;
        var borderFg = p.HeaderBorderFg.AnsiFg;
        var titleFg = p.PrimaryText.AnsiFg;

        // Design cards-states (workspace-7t0a.2): the [N] badge is a pressable key,
        // so it wears the interactive cyan — 'cyan = interactive keys only' holds.
        var badgeFg = accentFg;

        // Only the selected card shows the ▌ rail, in the design's focus-bar
        // muted green (--tr-focus-bar #5f875f; HeaderBorderFg #005f00 equals
        // the selection fill, which made the rail invisible on it —
        // workspace-7t0a.2). The Reading List no longer carries a permanent
        // cyan rail: per Launcher.dc.html it reads as a bookmark card whose
        // trailing ★ marks the slot.
        var accentBarColor = p.GetMutedFg().AnsiFg;
        var contentWidth = Math.Max(1, cellWidth - 1);

        // Selected subtitle turns the selection foreground (the design's
        // white-on-fill); the Reading List's empty-state prompt is quiet muted
        // copy, and domains keep the metadata green.
        string domainFg;
        if (isSelected)
        {
            domainFg = selFg;
        }
        else if (isReadingList && readingListCount is null or 0)
        {
            domainFg = p.GetMutedFg().AnsiFg;
        }
        else
        {
            domainFg = p.SecondaryText.AnsiFg;
        }

        // Title + subtitle centred in the content area (design 1a). The
        // separator slot (last line) stays reserved even on the bottom row —
        // where it renders as padding instead of a rule — so every row keeps
        // one geometry and the scroll math is untouched.
        var titleLine = Math.Max(0, (cellHeight - 1 - 2) / 2);
        var subtitleLine = titleLine + 1;

        // The separator rule is the cell's LAST line (workspace-stby/21uy). It's
        // the visual border between cell rows and must not be eaten by the
        // selection box — no selBg fill (workspace-63jj), but the selected
        // card's ▌ rail continues through it (workspace-7t0a.2). Rule color is
        // structural chrome (--tr-border #005f00). On the grid's bottom row
        // (drawSeparator false) the design draws NO rule — the line falls
        // through to the padding branch below so a selected bottom card's fill
        // reaches its bottom edge.
        if (lineIdx == cellHeight - 1 && drawSeparator)
        {
            var rule = $"{borderFg}{new string('─', Math.Max(0, cellWidth - (isSelected ? 1 : 0)))}{Reset}";
            return isSelected ? $"{accentBarColor}▌{Reset}{rule}" : rule;
        }

        if (lineIdx == titleLine)
        {
            // Title row: optional accent bar + leading space + NAME + glyph
            // (RL only, trailing) + right-aligned [N] badge. badgeZone floors
            // at 1 so the badgeless layout (items 10+) still reserves the
            // trailing cell — without it the row ran one cell past cellWidth
            // and shoved the next column's divider (workspace-7t0a.2).
            var glyphWidth = isReadingList ? 2 : 0;
            var badgeZone = badge.Length > 0 ? badge.Length + 1 : 1;

            // contentWidth - 1 reserves the leading space inside the highlight.
            var titleMax = Math.Max(1, contentWidth - 1 - glyphWidth - badgeZone);
            var truncName = RenderHelpers.TruncateText(name, titleMax);
            var gap = Math.Max(0, contentWidth - 1 - glyphWidth - truncName.Length - badgeZone);

            // The ★ trails the name in the metadata green (Launcher.dc.html —
            // the slot marker is quiet, not interactive cyan).
            var glyphFg = p.SecondaryText.AnsiFg;

            if (isSelected)
            {
                // Painted highlight: bar (inside selBg) + leading space +
                // title + glyph + pad + badge + trailing space, all inside
                // selBg so the box reads as a continuous rectangle. The
                // star's own SGR keeps selBg — emitting Reset between title
                // and glyph drops the bg (workspace-ktg4). The accent bar
                // is also inside selBg so column 0 isn't a black gap
                // (workspace-mj9x).
                var glyphPainted = isReadingList
                    ? $"{selFg} {glyphFg}{ReadingListGlyph}{selFg}"
                    : string.Empty;
                var sb = new System.Text.StringBuilder();
                sb.Append($"{selBg}{accentBarColor}▌");
                sb.Append($"{selBg}{selFg}{Bold} {truncName}{glyphPainted}{Reset}");
                sb.Append($"{selBg}{new string(' ', gap)}");
                if (badge.Length > 0)
                {
                    sb.Append($"{selBg}{badgeFg}{badge}{Reset}{selBg} {Reset}");
                }
                else
                {
                    sb.Append($"{selBg} {Reset}");
                }

                return sb.ToString();
            }

            var glyph = isReadingList ? $" {glyphFg}{ReadingListGlyph}{Reset}" : string.Empty;
            var titleSegment = $"{Bold}{titleFg}{truncName}{Reset}";
            if (badge.Length > 0)
            {
                return $"  {titleSegment}{glyph}{new string(' ', gap)}{badgeFg}{badge}{Reset} ";
            }

            return $"  {titleSegment}{glyph}{new string(' ', gap)} ";
        }

        if (lineIdx == subtitleLine)
        {
            // Subtitle row: optional accent bar + leading space + domain.
            var truncDomain = RenderHelpers.TruncateText(domain, Math.Max(1, contentWidth - 1));
            var pad = Math.Max(0, contentWidth - 1 - truncDomain.Length);

            if (isSelected)
            {
                // Bar inside selBg (workspace-mj9x) — no gap before the domain.
                return $"{selBg}{accentBarColor}▌{selBg}{domainFg} {truncDomain}{new string(' ', pad)}{Reset}";
            }

            return $"  {domainFg}{truncDomain}{Reset}{new string(' ', pad)}";
        }

        // Padding rows: the centring pads above the title and below the
        // subtitle, plus the bottom row's suppressed-separator line. When
        // selected they fill with selBg plus the accent ▌ bar, same as the
        // title/subtitle rows, so the selection rectangle is a continuous
        // block with no gap. Blank otherwise. (workspace-zlv0/mj9x/63jj:
        // selBg must reach the cell's top edge but must NOT bleed onto a
        // drawn separator row, which returns early above.)
        if (isSelected)
        {
            return $"{selBg}{accentBarColor}▌{selBg}{new string(' ', contentWidth)}{Reset}";
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
            name = "READING LIST ★";
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
            name = "LIST★";
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
    /// Renders one `[key]:action` footer hint (Launcher.dc.html,
    /// workspace-e86u): the key wears the interactive accent — 'cyan =
    /// interactive keys only' holds — while the brackets, colon, and label
    /// stay in the quiet muted tone. Visible length is
    /// <c>key.Length + action.Length + 3</c>.
    /// </summary>
    private static string FormatKbdHint(string key, string action, ThemePalette p)
    {
        var muted = p.GetMutedFg().AnsiFg;
        var accent = p.GetAccentFg().AnsiFg;
        return $"{muted}[{accent}{key}{muted}]:{action}{Reset}";
    }

    /// <summary>
    /// Builds the wordmark / narrow-title header as a list of lines suitable
    /// for inclusion in the launcher's virtual content stream.
    /// When <paramref name="showSetupHint"/> is true, the trailing blank line
    /// inside the box is replaced with an "Optional: add API keys" hint in the
    /// accent colour (workspace-ayt8, copy softened in workspace-4z4f). Net
    /// header height is unchanged.
    /// </summary>
#pragma warning disable SA1202 // exposed internal for LauncherTaglineAlignmentTests; private siblings precede.
    internal static List<string> BuildHeaderLines(int width, ThemePalette p, bool showSetupHint)
#pragma warning restore SA1202
    {
        var borderColor = p.GetDimFg().AnsiFg;
        var titleColor = p.HeaderTitleFg.AnsiFg;          // light pink (#ff87d7 ANSI 212)
        var titleColorDark = p.GetCelebrationFg().AnsiFg; // dark pink  (#ff5fd7 ANSI 206)
        var subtitle = "All copy, no nonsense.";
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
            // workspace-4z4f.1: the hint must read as optional — browsing
            // already works without keys; the keys unlock podcasts + AI layout.
            const string fullLabel = "→ Optional: add API keys for podcasts & AI layout";
            const string suffix = " · press c";
            var accent = p.GetAccentFg().AnsiFg;
            var muted = p.SecondaryText.AnsiFg;

            // Left-align with the tagline (workspace-jby8): the hint shares
            // the same indent as "All copy, no nonsense." so the two lines
            // form a single left edge under the wordmark, rather than the
            // hint being centred and floating away from the rest of the card.
            var hintPad = useLargeWordmark ? "      " : " ";
            var inner = Math.Max(0, boxOuter - 2);

            // Truncation order: drop suffix first, then truncate the label.
            var available = Math.Max(0, inner - hintPad.Length);
            var label = fullLabel;
            var includeSuffix = suffix.Length + label.Length <= available;
            if (!includeSuffix && label.Length > available)
            {
                label = label[..Math.Max(0, available)];
            }

            var visible = hintPad.Length + (includeSuffix ? label.Length + suffix.Length : label.Length);
            var rightPad = Math.Max(0, inner - visible);

            var rendered = includeSuffix
                ? $"{hintPad}{accent}{label}{Reset}{muted}{suffix}{Reset}{new string(' ', rightPad)}"
                : $"{hintPad}{accent}{label}{Reset}{new string(' ', rightPad)}";

            return $"{margin} {borderColor}│{Reset} {rendered} {borderColor}│{Reset}";
        }

        lines.Add($"{margin} {borderColor}╭{new string('─', boxOuter)}╮{Reset}");

        if (useLargeWordmark)
        {
            lines.Add(BlankBoxLine());
            for (var i = 0; i < Wordmark.Length; i++)
            {
                var rowColor = WordmarkUsesDark[i] ? titleColorDark : titleColor;
                lines.Add(BoxLine($"{rowColor}{Wordmark[i]}{Reset}", Wordmark[i].Length));
            }
        }
        else
        {
            lines.Add(BoxLine($" {titleColor}{"Wire Copy"}{Reset}", "Wire Copy".Length + 1));
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

        // workspace-0rde: drop the trailing blank-before-bottom-border when no
        // setup hint is shown — the bottom border already provides visual closure.
        // When the hint IS shown it occupies that slot, so header height is
        // 11/5 with hint and 10/4 without.
        if (showSetupHint)
        {
            lines.Add(SetupHintBoxLine());
        }

        lines.Add($"{margin} {borderColor}╰{new string('─', boxOuter)}╯{Reset}");

        return lines;
    }

    /// <summary>
    /// Builds the URL bar as a list of lines: blank, top border, content,
    /// bottom border (4 lines total).
    /// </summary>
    /// <remarks>
    /// workspace-0rde dropped the trailing blank that previously padded
    /// the URL bar from below; the first bookmark row's own top border now
    /// sits directly under the URL bar's bottom border.
    /// </remarks>
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
            return "nothing saved yet — press s to add";
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
        // Card stride (workspace-bs93/stby): centring pad + title + subtitle
        // + centring pad + separator. Mirrors LinkTreeRenderer's row layout —
        // a `│` divider between columns, with `┼` on the separator row so the
        // intersection reads as continuous. Responsive N columns (workspace-ehon):
        // loop over every column (was a 2-column special case) so a wide window
        // fills with more cards; the last column absorbs the width remainder so
        // the right edge stays flush and empty trailing cells continue the
        // separator rule. The BOTTOM row draws no rule (Launcher.dc.html,
        // workspace-e86u) — the footer's top rule caps the grid instead.
        if (line >= layout.CellHeight)
        {
            return new string(' ', layout.Width);
        }

        var totalRows = (totalItems + layout.Columns - 1) / layout.Columns;
        var drawSeparator = row < totalRows - 1;
        var isSeparatorLine = line == layout.CellHeight - 1 && drawSeparator;
        var sb = new System.Text.StringBuilder();

        for (var col = 0; col < layout.Columns; col++)
        {
            if (col > 0)
            {
                // Structural chrome (--tr-border #005f00, workspace-7t0a.2) — the
                // grid skeleton recedes behind the content instead of matching
                // metadata green.
                var divider = isSeparatorLine ? "┼" : "│";
                sb.Append($"{p.HeaderBorderFg.AnsiFg}{divider}{Reset}");
            }

            var itemIdx = (row * layout.Columns) + col;
            var isLastCol = col == layout.Columns - 1;
            var cellW = isLastCol
                ? ResponsiveGrid.LastCellWidthFor(layout.Width, layout.Columns)
                : layout.CellWidth;

            if (itemIdx < totalItems)
            {
                sb.Append(BuildCardCellLine(
                    bookmarks,
                    itemIdx,
                    selectedIndex,
                    cellW,
                    line,
                    layout.CellHeight,
                    p,
                    readingListCount,
                    drawSeparator));
            }
            else if (isSeparatorLine)
            {
                // Continue the separator rule across the empty cell so the
                // bottom edge reads as a single line.
                sb.Append($"{p.HeaderBorderFg.AnsiFg}{new string('─', cellW)}{Reset}");
            }
            else
            {
                sb.Append(new string(' ', cellW));
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

        // Build the launcher in three bands:
        //   header  — wordmark / title (setup hint inside header card when ShowSetupHint)
        //   urlBar  — URL bar (4 rows)
        //   grid    — bookmark rows
        var header = BuildHeaderLines(layout.Width, p, showSetupHint);
        var urlBar = BuildUrlBarLines(layout.Width, selectedIndex == -1, p);
        var grid = BuildBookmarkLines(bookmarks, selectedIndex, layout, variant, p, options.ReadingListItemCount);

        var topBands = header.Count + urlBar.Count;
        var totalContent = topBands + grid.Count;

        if (totalContent <= viewportHeight)
        {
            // Responsive fill (workspace-sf9o): the content fits without scrolling, so instead of
            // top-aligning it and leaving a large empty band above the bottom-pinned footer, keep the
            // wordmark + URL bar as a top "header" and vertically CENTER the bookmark grid in the space
            // beneath them — the launcher fills the window like the pre-wrapper terminal, and the
            // elements reflow with the window size. The header / URL-bar rows are unchanged, so the
            // URL-input cursor math (ComputeUrlBarInputRow) is unaffected.
            foreach (var line in header)
            {
                _helpers.WriteLine(line);
            }

            foreach (var line in urlBar)
            {
                _helpers.WriteLine(line);
            }

            var gridArea = Math.Max(0, viewportHeight - topBands);
            var gridPad = Math.Max(0, (gridArea - grid.Count) / 2);
            for (var i = 0; i < gridPad; i++)
            {
                _helpers.WriteLine(string.Empty);
            }

            foreach (var line in grid)
            {
                _helpers.WriteLine(line);
            }

            return;
        }

        // Overflow: scroll the combined stream as one unit (the wordmark / URL bar collapse upward).
        var content = new List<string>(totalContent);
        content.AddRange(header);
        content.AddRange(urlBar);
        content.AddRange(grid);

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

        // workspace-xx61: 'c' opens Setup (LauncherCommandHandler), NOT the
        // reading list — the old "[c] READING LIST" label was simply wrong (and a
        // first-run reading list is empty anyway). Point at what 'c' really does.
        var collLabel = "[c]  SETUP";
        var collPad = Math.Max(0, (width - collLabel.Length) / 2);
        _helpers.WriteLine(
            $"{new string(' ', collPad)}{p.SecondaryText.AnsiFg}[{Reset}{p.GetAccentFg().AnsiFg}c{Reset}{p.SecondaryText.AnsiFg}]{Reset}" +
            $"  {p.PrimaryText.AnsiFg}{Bold}SETUP{Reset}");

        var domainPad = Math.Max(0, (width - "API keys & settings".Length) / 2);
        _helpers.WriteLine($"{new string(' ', domainPad + 5)}{p.SecondaryText.AnsiFg}{Dim}API keys & settings{Reset}");
    }
}
