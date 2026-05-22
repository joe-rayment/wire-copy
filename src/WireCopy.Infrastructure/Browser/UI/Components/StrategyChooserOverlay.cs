// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Anchored modal for the Ctrl+L layout chooser (workspace-ujxu). Replaces
/// the previous status-bar-only progress messaging with a Helix-style box
/// pinned to the bottom of the screen showing one row per strategy with
/// its current state (pre-flight checkbox, probing spinner, ready/✗).
///
/// <para>
/// Painted by <c>BrowserOrchestrator.RenderCurrentPageAsync</c> via the
/// <c>_activeOverlayPainter</c> hook so the modal repaints alongside the
/// underlying page render — keeps the previewed link tree visible above
/// it as the user cycles ◀/▶ in the preview phase.
/// </para>
/// </summary>
internal static class StrategyChooserOverlay
{
    /// <summary>Spinner frames matching the previous status-bar spinner (StrategyChooserHandler.Spinner).</summary>
    public static readonly string[] Spinner =
    {
        "⠋", "⠙", "⠹", "⠸",
        "⠼", "⠴", "⠦", "⠧",
        "⠇", "⠏",
    };

    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";
    private const string Bold = "\x1b[1m";

    public enum Phase
    {
        /// <summary>User picks which strategies to probe via checkboxes.</summary>
        Preflight,

        /// <summary>Probes are running; each row shows its live state.</summary>
        Probing,

        /// <summary>Strategies are ready; user cycles ◀/▶ to preview candidates.</summary>
        Preview,
    }

    public enum RowState
    {
        /// <summary>Pre-flight: not yet probed.</summary>
        Pending,

        /// <summary>Currently being probed.</summary>
        Probing,

        /// <summary>Probe succeeded — strategy is selectable in preview.</summary>
        Available,

        /// <summary>Probe failed or strategy was excluded — disabled row.</summary>
        Unavailable,
    }

    /// <summary>
    /// Paints the overlay anchored to the bottom of the terminal (above the
    /// status bar). The caller is expected to have just finished rendering
    /// the underlying page; this paints OVER the bottom rows without
    /// touching the rest of the screen.
    /// </summary>
    public static void Render(State state, ThemePalette palette, int terminalWidth, int terminalHeight)
    {
        if (state.Rows.Count == 0)
        {
            return;
        }

        var title = state.CurrentPhase switch
        {
            Phase.Preflight => "Layout strategies — choose which to probe",
            Phase.Probing => "Loading layout strategies…",
            Phase.Preview => "Layout preview",
            _ => "Layout strategies",
        };

        var hintLine = state.CurrentPhase switch
        {
            Phase.Preflight => "Space:toggle  Enter:probe selected  Esc:cancel",
            Phase.Probing => "Probing…",
            Phase.Preview => "◀/▶:cycle  Enter:save  Esc:cancel  Shift+I:guidance (AI)",
            _ => string.Empty,
        };

        // Pre-format each row's plain text + styled text. We need the plain
        // length to right-pad the inner content to a consistent width.
        var maxRowWidth = 0;
        var prepared = new (string Plain, string Styled)[state.Rows.Count];
        for (var i = 0; i < state.Rows.Count; i++)
        {
            var rowItem = state.Rows[i];
            var isCursor = i == state.Cursor;
            prepared[i] = FormatRow(rowItem, state.CurrentPhase, isCursor, palette);
            if (prepared[i].Plain.Length > maxRowWidth)
            {
                maxRowWidth = prepared[i].Plain.Length;
            }
        }

        // Inner width = larger of (rows, title, hint) + a little padding.
        var innerContent = Math.Max(maxRowWidth, Math.Max(title.Length + 4, hintLine.Length));
        var innerWidth = Math.Clamp(innerContent + 2, 30, Math.Max(30, terminalWidth - 4));

        var contentLines = state.Rows.Count;
        if (!string.IsNullOrEmpty(state.Footnote))
        {
            contentLines++;
        }

        contentLines++; // hint line inside the box

        var boxHeight = contentLines + 2; // +2 for borders
        var startRow = Math.Max(1, terminalHeight - boxHeight - 1); // -1 for status bar
        var startCol = Math.Max(0, (terminalWidth - innerWidth - 2) / 2);

        var borderColor = palette.SecondaryText.AnsiFg;
        var titleColor = palette.StatusBarTextFg.AnsiFg;

        // Top border with title
        var titlePadded = $" {title} ";
        var totalDashes = innerWidth + 2;
        var rightDashCount = Math.Max(0, totalDashes - titlePadded.Length - 1);
        var topLine = $"{borderColor}┌─{Reset}{titleColor}{titlePadded}{Reset}{borderColor}{new string('─', rightDashCount)}┐{Reset}";

        Console.SetCursorPosition(startCol, startRow);
        Console.Write(topLine);

        var row = startRow + 1;
        for (var i = 0; i < prepared.Length && row < terminalHeight - 1; i++, row++)
        {
            var (plain, styled) = prepared[i];
            var pad = Math.Max(0, innerWidth - plain.Length);
            Console.SetCursorPosition(startCol, row);
            Console.Write($"{borderColor}│{Reset} {styled}{new string(' ', pad)} {borderColor}│{Reset}");
        }

        if (!string.IsNullOrEmpty(state.Footnote) && row < terminalHeight - 1)
        {
            var note = state.Footnote!;
            if (note.Length > innerWidth)
            {
                note = note[..innerWidth];
            }

            var notePad = Math.Max(0, innerWidth - note.Length);
            Console.SetCursorPosition(startCol, row);
            Console.Write($"{borderColor}│{Reset} {Dim}{palette.SecondaryText.AnsiFg}{note}{Reset}{new string(' ', notePad)} {borderColor}│{Reset}");
            row++;
        }

        if (row < terminalHeight - 1)
        {
            var hint = hintLine;
            if (hint.Length > innerWidth)
            {
                hint = hint[..innerWidth];
            }

            var hintPad = Math.Max(0, innerWidth - hint.Length);
            Console.SetCursorPosition(startCol, row);
            Console.Write($"{borderColor}│{Reset} {palette.GetAccentFg().AnsiFg}{hint}{Reset}{new string(' ', hintPad)} {borderColor}│{Reset}");
            row++;
        }

        if (row < terminalHeight - 1)
        {
            Console.SetCursorPosition(startCol, row);
            Console.Write($"{borderColor}└{new string('─', innerWidth + 2)}┘{Reset}");
        }
    }

    private static (string Plain, string Styled) FormatRow(
        Row row,
        Phase phase,
        bool isCursor,
        ThemePalette palette)
    {
        var nameWidth = 16; // pad strategy name column so detail aligns

        // Phase-dependent icon at column 0
        string iconPlain;
        string iconStyled;
        switch (phase)
        {
            case Phase.Preflight:
                iconPlain = row.Selected ? "[x]" : "[ ]";
                iconStyled = row.Selected
                    ? $"{palette.GetAccentFg().AnsiFg}[x]{Reset}"
                    : $"{palette.SecondaryText.AnsiFg}[ ]{Reset}";
                break;

            case Phase.Probing:
                iconPlain = row.State switch
                {
                    RowState.Pending => " · ",
                    RowState.Probing => $" {Spinner[row.SpinnerFrame % Spinner.Length]} ",
                    RowState.Available => " ✓ ",
                    RowState.Unavailable => " ✗ ",
                    _ => "   ",
                };
                iconStyled = row.State switch
                {
                    RowState.Pending => $"{palette.SecondaryText.AnsiFg}{iconPlain}{Reset}",
                    RowState.Probing => $"{palette.GetAccentFg().AnsiFg}{iconPlain}{Reset}",
                    RowState.Available => $"{palette.GetAccentFg().AnsiFg}{iconPlain}{Reset}",
                    RowState.Unavailable => $"{palette.SecondaryText.AnsiFg}{iconPlain}{Reset}",
                    _ => iconPlain,
                };
                break;

            case Phase.Preview:
                iconPlain = isCursor ? " ▶ " : "   ";
                iconStyled = isCursor
                    ? $"{palette.GetAccentFg().AnsiFg}{Bold}{iconPlain}{Reset}"
                    : iconPlain;
                break;

            default:
                iconPlain = "   ";
                iconStyled = iconPlain;
                break;
        }

        // Name column — bold when active in preview phase, dim when unavailable.
        var name = row.DisplayName;
        if (name.Length > nameWidth)
        {
            name = name[..nameWidth];
        }

        var namePadded = name.PadRight(nameWidth);
        string nameStyled;
        if (phase == Phase.Preview && isCursor)
        {
            nameStyled = $"{Bold}{palette.PrimaryText.AnsiFg}{namePadded}{Reset}";
        }
        else if (row.State == RowState.Unavailable)
        {
            nameStyled = $"{Dim}{palette.SecondaryText.AnsiFg}{namePadded}{Reset}";
        }
        else
        {
            nameStyled = $"{palette.PrimaryText.AnsiFg}{namePadded}{Reset}";
        }

        // Detail column — dim secondary text.
        var detail = row.Detail ?? string.Empty;
        var detailStyled = detail.Length > 0
            ? $"{Dim}{palette.SecondaryText.AnsiFg}{detail}{Reset}"
            : string.Empty;

        var plain = $"{iconPlain} {namePadded} {detail}".TrimEnd();
        var styled = $"{iconStyled} {nameStyled} {detailStyled}".TrimEnd();
        return (plain, styled);
    }

    public sealed class Row
    {
        public required string Id { get; init; }

        public required string DisplayName { get; init; }

        /// <summary>Optional one-line detail rendered after the name (e.g. "348 links", "up to 5s", "No RSS/Atom feed").</summary>
        public string? Detail { get; set; }

        public RowState State { get; set; } = RowState.Pending;

        /// <summary>Pre-flight checkbox state.</summary>
        public bool Selected { get; set; } = true;

        /// <summary>Spinner animation frame index (modulo <see cref="Spinner"/>.Length).</summary>
        public int SpinnerFrame { get; set; }
    }

    public sealed class State
    {
        public Phase CurrentPhase { get; set; } = Phase.Preflight;

        public List<Row> Rows { get; } = new();

        /// <summary>Cursor row index in Preflight phase. Selected row in Preview phase.</summary>
        public int Cursor { get; set; }

        /// <summary>Optional small footnote rendered above the keybinding hint line.</summary>
        public string? Footnote { get; set; }
    }
}
