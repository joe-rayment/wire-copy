// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Animations;

/// <summary>
/// Brief centered "layout name" flash fired when a layout / strategy is applied.
/// Renders the name in <see cref="ThemePalette.GetCelebrationFg"/> (hot pink) bold,
/// holds for ~500ms, then clears the row. Synchronous blocking call.
/// </summary>
internal static class LayoutFlashAnimation
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const int HoldMs = 500;
    private const int MaxLabelWidth = 60;

    /// <summary>
    /// Plays the layout-flash animation. Shows <paramref name="layoutName"/> centered
    /// horizontally at the terminal's vertical midpoint for ~500ms, then clears it.
    /// </summary>
    /// <param name="layoutName">Strategy / layout display name to flash.</param>
    /// <param name="palette">Theme palette — uses CelebrationFg for the text.</param>
    /// <param name="terminalWidth">Current terminal width in columns.</param>
    /// <param name="terminalHeight">Current terminal height in rows.</param>
    public static void Play(string layoutName, ThemePalette palette, int terminalWidth, int terminalHeight)
    {
        if (string.IsNullOrWhiteSpace(layoutName) || Console.IsOutputRedirected)
        {
            return;
        }

        var label = layoutName.Length > MaxLabelWidth
            ? string.Concat(layoutName.AsSpan(0, MaxLabelWidth - 1), "…")
            : layoutName;

        var labelWidth = label.Length;
        if (labelWidth + 2 > terminalWidth)
        {
            return;
        }

        var row = Math.Max(0, terminalHeight / 2);
        var col = Math.Max(0, (terminalWidth - labelWidth) / 2);
        var color = palette.GetCelebrationFg().AnsiFg;

        lock (ConsoleSync.Lock)
        {
            try
            {
                Console.SetCursorPosition(col, row);
                Console.Write($"{color}{Bold}{label}{Reset}");
            }
            catch
            {
                return;
            }
        }

        Thread.Sleep(HoldMs);

        lock (ConsoleSync.Lock)
        {
            try
            {
                Console.SetCursorPosition(col, row);
                Console.Write(new string(' ', labelWidth));
            }
            catch
            {
                // Non-standard console — leave the label visible; orchestrator's
                // next render pass will repaint.
            }
        }
    }
}
