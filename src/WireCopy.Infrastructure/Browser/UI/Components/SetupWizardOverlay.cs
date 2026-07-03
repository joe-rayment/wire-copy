// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// workspace-5oe9.8 — anchored multi-line "card" modal for the AI setup wizard.
/// Unlike <see cref="StrategyChooserOverlay"/> (single-line strategy rows), this
/// renders one question/confirmation card at a time, each surfacing the DURABLE
/// identifier (CSS selector / URL pattern) for every option so the user SEES
/// what will be saved ("the main story is section.lead").
/// </summary>
internal static class SetupWizardOverlay
{
    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";
    private const string Bold = "\x1b[1m";

    public enum Mode
    {
        /// <summary>A model round-trip is in flight; show a spinner + label.</summary>
        Analyzing,

        /// <summary>A question/confirmation card is shown.</summary>
        Card,
    }

    public static void Render(State state, ThemePalette palette, int terminalWidth, int terminalHeight)
    {
        // workspace-f25j.4: the box must fit the terminal — title row + content
        // + bottom border + 1-row margins. Anything past this capacity is
        // windowed (scrolled to the focused option) instead of silently clipped.
        var maxContentLines = Math.Max(1, terminalHeight - 4);
        var lines = BuildLines(state, maxContentLines, out var title);
        if (lines.Count == 0)
        {
            return;
        }

        var inner = lines.Select(PlainLength).DefaultIfEmpty(0).Max();
        inner = Math.Max(inner, title.Length + 4);
        var innerWidth = Math.Clamp(inner + 2, 36, Math.Max(36, terminalWidth - 4));

        var boxHeight = lines.Count + 2;
        var startRow = Math.Max(1, terminalHeight - boxHeight - 1);

        // workspace-s621: terminalWidth is the dock-aware width; add the dock origin.
        var startCol = OverlayViewport.Left + Math.Max(0, (terminalWidth - innerWidth - 2) / 2);

        var border = palette.SecondaryText.AnsiFg;
        var titleColor = palette.StatusBarTextFg.AnsiFg;

        var titlePadded = $" {title} ";
        var rightDashes = Math.Max(0, innerWidth + 2 - titlePadded.Length - 1);
        Console.SetCursorPosition(startCol, startRow);
        Console.Write($"{border}┌─{Reset}{titleColor}{titlePadded}{Reset}{border}{new string('─', rightDashes)}┐{Reset}");

        var row = startRow + 1;
        foreach (var (plain, styled) in lines)
        {
            if (row >= terminalHeight - 1)
            {
                break;
            }

            var pad = Math.Max(0, innerWidth - PlainWidth(plain));
            Console.SetCursorPosition(startCol, row);
            Console.Write($"{border}│{Reset} {styled}{new string(' ', pad)} {border}│{Reset}");
            row++;
        }

        if (row < terminalHeight - 1)
        {
            Console.SetCursorPosition(startCol, row);
            Console.Write($"{border}└{new string('─', innerWidth + 2)}┘{Reset}");
        }

        // Local helpers capture the palette for styled segments.
        (string Plain, string Styled) PlainStyled(string plain, string styled) => (plain, styled);

        List<(string Plain, string Styled)> BuildLines(State s, int maxContent, out string boxTitle)
        {
            var result = new List<(string, string)>();
            if (s.Mode == Mode.Analyzing)
            {
                boxTitle = "Set up this site with AI";
                var spinner = StrategyChooserOverlay.Spinner[s.SpinnerFrame % StrategyChooserOverlay.Spinner.Length];
                var label = string.IsNullOrEmpty(s.AnalyzingLabel) ? "Working…" : s.AnalyzingLabel!;
                result.Add(PlainStyled($"{spinner} {label}", $"{palette.GetAccentFg().AnsiFg}{spinner}{Reset} {label}"));
                return result;
            }

            var card = s.Card;
            boxTitle = card?.Title ?? "Set up this site with AI";
            if (card == null)
            {
                return result;
            }

            // workspace-f25j.4: window the option rows so the focused option is
            // always on screen, with an explicit "N more" note instead of a
            // silent clip.
            var window = ComputeCardWindow(card, maxContent);

            if (!string.IsNullOrEmpty(card.Prompt))
            {
                result.Add(PlainStyled(card.Prompt, $"{Bold}{palette.PrimaryText.AnsiFg}{card.Prompt}{Reset}"));
                result.Add(PlainStyled(string.Empty, string.Empty));
            }

            for (var i = window.OptionOffset; i < window.OptionOffset + window.OptionCount; i++)
            {
                var opt = card.Options[i];
                var isCursor = i == card.Cursor;
                var marker = isCursor ? "▸" : " ";
                var idText = opt.Identifier.Length > 0 ? $"   {opt.Identifier}" : string.Empty;
                var plain = $"{marker} {opt.Label}{idText}";
                var nameStyled = isCursor
                    ? $"{Bold}{palette.GetAccentFg().AnsiFg}{marker} {opt.Label}{Reset}"
                    : $"{palette.PrimaryText.AnsiFg}{marker} {opt.Label}{Reset}";
                var idStyled = opt.Identifier.Length > 0
                    ? $"{Dim}{palette.SecondaryText.AnsiFg}   {opt.Identifier}{Reset}"
                    : string.Empty;
                result.Add(PlainStyled(plain, $"{nameStyled}{idStyled}"));
            }

            if (!string.IsNullOrEmpty(window.Footnote))
            {
                result.Add(PlainStyled(string.Empty, string.Empty));
                result.Add(PlainStyled(window.Footnote!, $"{Dim}{palette.SecondaryText.AnsiFg}{window.Footnote}{Reset}"));
            }

            if (!string.IsNullOrEmpty(card.Hint))
            {
                result.Add(PlainStyled(card.Hint!, $"{palette.GetAccentFg().AnsiFg}{card.Hint}{Reset}"));
            }

            return result;
        }
    }

    /// <summary>
    /// Test/diagnostic helper: the plain-text lines a card renders, including the
    /// durable identifier on each option. Used by component tests to assert the
    /// identifier is visible without scraping the ANSI Console output.
    /// </summary>
    public static IReadOnlyList<string> DescribeCard(WizardCard card, int? maxContentLines = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var window = ComputeCardWindow(card, maxContentLines ?? int.MaxValue);
        var lines = new List<string> { card.Title };
        if (!string.IsNullOrEmpty(card.Prompt))
        {
            lines.Add(card.Prompt);
        }

        for (var i = window.OptionOffset; i < window.OptionOffset + window.OptionCount; i++)
        {
            var opt = card.Options[i];
            var id = opt.Identifier.Length > 0 ? $"   {opt.Identifier}" : string.Empty;
            lines.Add($"{opt.Label}{id}");
        }

        if (!string.IsNullOrEmpty(window.Footnote))
        {
            lines.Add(window.Footnote!);
        }

        return lines;
    }

    /// <summary>
    /// workspace-f25j.4: which slice of a card's options fits into
    /// <paramref name="maxContentLines"/> content rows alongside the fixed
    /// prompt/footnote/hint rows. When everything fits this is the whole list
    /// and the untouched footnote; when it does not, the window scrolls to keep
    /// <see cref="WizardCard.Cursor"/> visible and the footnote gains an
    /// explicit "↑/↓ N more" overflow note (created if the card had none).
    /// </summary>
    internal static CardWindow ComputeCardWindow(WizardCard card, int maxContentLines)
    {
        ArgumentNullException.ThrowIfNull(card);
        var promptRows = string.IsNullOrEmpty(card.Prompt) ? 0 : 2;
        var hintRows = string.IsNullOrEmpty(card.Hint) ? 0 : 1;
        var footnoteRows = string.IsNullOrEmpty(card.Footnote) ? 0 : 2;

        if (promptRows + card.Options.Count + footnoteRows + hintRows <= maxContentLines)
        {
            return new CardWindow(0, card.Options.Count, card.Footnote);
        }

        // Overflowing: the footnote block always renders (it carries the note).
        var maxVisible = Math.Max(1, maxContentLines - promptRows - hintRows - 2);
        var (offset, visible) = ComputeOptionWindow(card.Options.Count, card.Cursor, maxVisible);
        var hiddenAbove = offset;
        var hiddenBelow = card.Options.Count - offset - visible;

        var noteParts = new List<string>(2);
        if (hiddenAbove > 0)
        {
            noteParts.Add($"↑ {hiddenAbove} more above");
        }

        if (hiddenBelow > 0)
        {
            noteParts.Add($"↓ {hiddenBelow} more below");
        }

        var note = string.Join(" · ", noteParts);
        string? footnote;
        if (note.Length == 0)
        {
            footnote = card.Footnote;
        }
        else if (string.IsNullOrEmpty(card.Footnote))
        {
            footnote = note;
        }
        else
        {
            footnote = $"{card.Footnote} · {note}";
        }

        return new CardWindow(offset, visible, footnote);
    }

    /// <summary>
    /// workspace-f25j.4: first visible option index + visible count for a list
    /// of <paramref name="optionCount"/> rows of which at most
    /// <paramref name="maxVisible"/> fit, scrolled so <paramref name="cursor"/>
    /// stays inside the window.
    /// </summary>
    internal static (int Offset, int Visible) ComputeOptionWindow(int optionCount, int cursor, int maxVisible)
    {
        maxVisible = Math.Max(1, maxVisible);
        if (optionCount <= maxVisible)
        {
            return (0, optionCount);
        }

        var clampedCursor = Math.Clamp(cursor, 0, optionCount - 1);
        var offset = Math.Clamp(clampedCursor - maxVisible + 1, 0, optionCount - maxVisible);
        return (offset, maxVisible);
    }

    private static int PlainLength((string Plain, string Styled) line) => PlainWidth(line.Plain);

    private static int PlainWidth(string plain) => plain.Length;

    public sealed class State
    {
        public Mode Mode { get; set; } = Mode.Analyzing;

        public string? AnalyzingLabel { get; set; }

        public int SpinnerFrame { get; set; }

        public WizardCard? Card { get; set; }
    }

    public sealed class WizardCard
    {
        public required string Title { get; init; }

        public string Prompt { get; init; } = string.Empty;

        public List<CardOption> Options { get; init; } = new();

        public int Cursor { get; set; }

        public string Hint { get; init; } = string.Empty;

        public string? Footnote { get; init; }
    }

    public sealed class CardOption
    {
        public required string Label { get; init; }

        /// <summary>The durable identifier (selector / url-pattern) shown to the user.</summary>
        public string Identifier { get; init; } = string.Empty;

        /// <summary>
        /// workspace-wylw: CSS selector evaluated on the sidecar lens when this
        /// option is focused, highlighting every link it matches. Empty clears
        /// the highlight instead.
        /// </summary>
        public string HighlightSelector { get; init; } = string.Empty;
    }

    /// <summary>workspace-f25j.4: the visible option slice + effective footnote for a card.</summary>
    internal sealed record CardWindow(int OptionOffset, int OptionCount, string? Footnote);
}
