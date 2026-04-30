// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI;

/// <summary>
/// Plays a decrypt-reveal animation on the page title after navigation.
/// The title resolves from noise characters through random letters to the correct text,
/// sweeping left-to-right across 8 frames at 50ms each (400ms total).
/// </summary>
internal static class DecryptRevealAnimation
{
    private const int FrameCount = 8;
    private const int FrameDelayMs = 50;
    private const string Reset = "\x1b[0m";

    // Noise characters for frames 1-2
    private static readonly char[] NoiseChars = ['\u2591', '\u2592', '\u2593']; // ░▒▓

    /// <summary>
    /// Plays the decrypt-reveal animation for the given title text.
    /// Synchronous blocking call — uses Thread.Sleep between frames.
    /// </summary>
    /// <param name="title">The final title text to reveal.</param>
    /// <param name="row">Console row where the title is rendered.</param>
    /// <param name="col">Console column where the title text begins.</param>
    /// <param name="palette">Theme palette for color selection.</param>
    public static void Play(string title, int row, int col, ThemePalette palette)
    {
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        var random = new Random();
        var dimFg = palette.GetDimFg().AnsiFg;
        var mutedFg = palette.GetMutedFg().AnsiFg;
        var titleFg = $"{palette.HeaderTitleFg.AnsiFg}\x1b[1m"; // Bold, matching RenderHeader

        for (var frame = 1; frame <= FrameCount; frame++)
        {
            // Calculate how far the "reveal sweep" has progressed (0.0 to 1.0)
            var revealProgress = (double)frame / FrameCount;
            var revealedCount = (int)(title.Length * revealProgress);

            var output = new char[title.Length];
            var colorSegments = new List<(int Start, int Length, string Color)>();
            var segStart = 0;
            string? currentColor = null;

            for (var i = 0; i < title.Length; i++)
            {
                string charColor;
                char displayChar;

                if (i < revealedCount && frame >= 6)
                {
                    // Frames 6-8: correct characters sweep in left-to-right
                    displayChar = title[i];
                    charColor = titleFg;
                }
                else if (i < revealedCount && frame >= 3)
                {
                    // Frames 3-5: random ASCII letters replacing noise left-to-right
                    displayChar = title[i] == ' ' ? ' ' : (char)('A' + random.Next(26));
                    charColor = mutedFg;
                }
                else
                {
                    // Frames 1-2 (and unrevealed portion of 3-5): noise characters
                    displayChar = title[i] == ' ' ? ' ' : NoiseChars[random.Next(NoiseChars.Length)];
                    charColor = dimFg;
                }

                output[i] = displayChar;

                // Track color segments for efficient ANSI output
                if (charColor != currentColor)
                {
                    if (currentColor != null)
                    {
                        colorSegments.Add((segStart, i - segStart, currentColor));
                    }

                    segStart = i;
                    currentColor = charColor;
                }
            }

            // Final segment
            if (currentColor != null)
            {
                colorSegments.Add((segStart, title.Length - segStart, currentColor));
            }

            // Build the ANSI-colored output string
            var ansiOutput = new System.Text.StringBuilder();
            foreach (var (start, length, color) in colorSegments)
            {
                ansiOutput.Append(color);
                ansiOutput.Append(output, start, length);
            }

            ansiOutput.Append(Reset);

            // Write the frame with thread-safe console access
            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(col, row);
                    Console.Write(ansiOutput.ToString());
                }
                catch
                {
                    // Non-standard console — silently skip
                    return;
                }
            }

            if (frame < FrameCount)
            {
                Thread.Sleep(FrameDelayMs);
            }
        }
    }
}
