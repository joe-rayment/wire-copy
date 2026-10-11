// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared plumbing for the bordered "card" overlays (<see cref="SetupWizardOverlay"/>,
/// <see cref="StrategyChooserOverlay"/>). Each overlay lays its box out as a list of
/// <see cref="Op"/>s (cursor position + text) so the geometry is testable without a
/// real terminal; <see cref="Paint"/> then plays them onto the console.
/// </summary>
internal static class OverlayBox
{
    /// <summary>
    /// A card row is <c>│ </c> + inner-width cells + <c> │</c>, i.e. this many columns wider than its
    /// content area.
    /// </summary>
    internal const int Chrome = 4;

    /// <summary>Plays a laid-out frame onto the console.</summary>
    public static void Paint(IEnumerable<Op> ops)
    {
        foreach (var op in ops)
        {
            Console.SetCursorPosition(op.Col, op.Row);
            Console.Write(op.Text);
        }
    }

    /// <summary>
    /// The content-area width of a card: <paramref name="wanted"/> (but at least <paramref name="min"/>),
    /// capped so the whole box (content + <see cref="Chrome"/>) fits <paramref name="terminalWidth"/>.
    /// </summary>
    internal static int InnerWidth(int wanted, int min, int terminalWidth)
        => Math.Min(Math.Max(wanted, min), Math.Max(1, terminalWidth - Chrome));

    /// <summary>
    /// Left column of a box with <paramref name="innerWidth"/> content cells, centred in the dock-aware
    /// <paramref name="terminalWidth"/> (workspace-s621) so its right border lands on the last column at most.
    /// </summary>
    internal static int StartCol(int terminalWidth, int innerWidth)
        => OverlayViewport.Left + Math.Max(0, (terminalWidth - innerWidth - Chrome) / 2);

    /// <summary>
    /// Cuts a row to at most <paramref name="width"/> visible cells. When it must cut, one cell is
    /// reserved for a trailing <c>…</c>. <paramref name="styled"/> keeps every ANSI escape sequence it
    /// had before the cut (so colours stay balanced) and gains a reset after the ellipsis; text that
    /// already fits is returned untouched. Cells are counted like <see cref="RenderHelpers.GetDisplayWidth"/>
    /// (wide CJK = 2, a surrogate pair = 2, combining marks = 0).
    /// </summary>
    internal static (string Plain, string Styled) Fit(string plain, string styled, int width)
    {
        if (width <= 0)
        {
            return (string.Empty, string.Empty);
        }

        if (RenderHelpers.GetDisplayWidth(plain) <= width && RenderHelpers.GetDisplayWidth(styled) <= width)
        {
            return (plain, styled);
        }

        return (Cut(plain, width, appendReset: false), Cut(styled, width, appendReset: true));
    }

    private static string Cut(string text, int width, bool appendReset)
    {
        var limit = width - 1; // the ellipsis takes the last cell
        var sb = new StringBuilder();
        var cells = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // Copy a whole ESC [ ... <final letter> sequence; it occupies no cells.
            if (c == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var j = i + 2;
                while (j < text.Length && !char.IsAsciiLetter(text[j]))
                {
                    j++;
                }

                j = Math.Min(j + 1, text.Length);
                sb.Append(text, i, j - i);
                i = j;
                continue;
            }

            var unit = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var cw = unit == 2 ? 2 : RenderHelpers.GetCharDisplayWidth(c);
            if (cells + cw > limit)
            {
                break;
            }

            sb.Append(text, i, unit);
            cells += cw;
            i += unit;
        }

        sb.Append('\u2026');
        if (appendReset)
        {
            sb.Append("\x1b[0m");
        }

        return sb.ToString();
    }

    /// <summary>One absolutely-positioned write: move to (<see cref="Col"/>, <see cref="Row"/>), emit <see cref="Text"/>.</summary>
    internal readonly record struct Op(int Col, int Row, string Text);
}
