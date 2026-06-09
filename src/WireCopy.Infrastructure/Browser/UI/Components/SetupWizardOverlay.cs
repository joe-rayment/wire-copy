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
        var lines = BuildLines(state, out var title);
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

        List<(string Plain, string Styled)> BuildLines(State s, out string boxTitle)
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

            if (!string.IsNullOrEmpty(card.Prompt))
            {
                result.Add(PlainStyled(card.Prompt, $"{Bold}{palette.PrimaryText.AnsiFg}{card.Prompt}{Reset}"));
                result.Add(PlainStyled(string.Empty, string.Empty));
            }

            for (var i = 0; i < card.Options.Count; i++)
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

            if (!string.IsNullOrEmpty(card.Footnote))
            {
                result.Add(PlainStyled(string.Empty, string.Empty));
                result.Add(PlainStyled(card.Footnote!, $"{Dim}{palette.SecondaryText.AnsiFg}{card.Footnote}{Reset}"));
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
    public static IReadOnlyList<string> DescribeCard(WizardCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var lines = new List<string> { card.Title };
        if (!string.IsNullOrEmpty(card.Prompt))
        {
            lines.Add(card.Prompt);
        }

        foreach (var opt in card.Options)
        {
            var id = opt.Identifier.Length > 0 ? $"   {opt.Identifier}" : string.Empty;
            lines.Add($"{opt.Label}{id}");
        }

        if (!string.IsNullOrEmpty(card.Footnote))
        {
            lines.Add(card.Footnote!);
        }

        return lines;
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
    }
}
