// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared plumbing for the bordered "card" overlays (<see cref="SetupWizardOverlay"/>,
/// <see cref="StrategyChooserOverlay"/>). Each overlay lays its box out as a list of
/// <see cref="Op"/>s (cursor position + text) so the geometry is testable without a
/// real terminal; <see cref="Paint"/> then plays them onto the console.
/// </summary>
internal static class OverlayBox
{
    /// <summary>One absolutely-positioned write: move to (<see cref="Col"/>, <see cref="Row"/>), emit <see cref="Text"/>.</summary>
    internal readonly record struct Op(int Col, int Row, string Text);

    /// <summary>Plays a laid-out frame onto the console.</summary>
    public static void Paint(IEnumerable<Op> ops)
    {
        foreach (var op in ops)
        {
            Console.SetCursorPosition(op.Col, op.Row);
            Console.Write(op.Text);
        }
    }
}
