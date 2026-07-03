// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Animations;

/// <summary>
/// A 4-phase celebration animation that fires after successful podcast generation.
/// Total duration: 1600ms (flash 200ms, sparkle 400ms, typewriter 600ms, settle 400ms).
/// </summary>
internal static class CelebrationAnimation
{
    private const string Reset = "\x1b[0m";
    private const char SparkleA = '\u2726'; // ✦
    private const char SparkleB = '\u2727'; // ✧
    private const int FlashFrameMs = 100;
    private const int SparkleFrameMs = 100;
    private const int SettleFrameMs = 100;

    /// <summary>
    /// Plays the celebration animation synchronously. Safe to call after a long-running
    /// podcast generation — blocks the thread with Thread.Sleep.
    /// All console access is guarded by <see cref="ConsoleSync.Lock"/>.
    /// </summary>
    /// <param name="palette">The current theme palette.</param>
    /// <param name="message">The celebration message to display (e.g. "Podcast ready! 5 chapters - 12m 30s").</param>
    /// <param name="terminalWidth">Current terminal width in columns.</param>
    /// <param name="terminalHeight">Current terminal height in rows.</param>
    /// <param name="seed">Random seed for deterministic sparkle positions (null for non-deterministic).</param>
    public static void Play(
        ThemePalette palette,
        string message,
        int terminalWidth,
        int terminalHeight,
        int? seed = null)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var celebrationColor = palette.GetCelebrationFg().AnsiFg;
        var normalBorder = palette.HeaderBorderFg.AnsiFg;

        // Calculate message placement: centered horizontally and vertically
        var boxWidth = Math.Min(message.Length + 6, terminalWidth - 4);
        var boxLeft = Math.Max(0, (terminalWidth - boxWidth) / 2);
        var boxTop = Math.Max(0, (terminalHeight / 2) - 2);

        // The message area is a 3-line box: top border, message, bottom border
        var innerWidth = boxWidth - 2;
        var displayMessage = message.Length > innerWidth
            ? message[..(innerWidth - 1)] + "\u2026"
            : message;

        // Bounding box for sparkle placement (a region around the message box)
        var sparkleTop = Math.Max(0, boxTop - 2);
        var sparkleBottom = Math.Min(terminalHeight - 1, boxTop + 4);
        var sparkleLeft = Math.Max(0, boxLeft - 3);
        var sparkleRight = Math.Min(terminalWidth - 1, boxLeft + boxWidth + 3);

        // Track sparkle positions for the settle phase
        var sparklePositions = new List<(int Row, int Col)>();

        // === Phase 1: Flash (200ms) — 2 frames x 100ms ===
        for (var frame = 0; frame < 2; frame++)
        {
            // workspace-khpe.1: box coordinates were computed once from the
            // starting dimensions; a resize mid-animation would paint the box at
            // stale positions and corrupt the display. Bail as soon as the
            // terminal changes size — the next full page render repaints cleanly.
            if (DimensionsChanged(terminalWidth, terminalHeight))
            {
                return;
            }

            var borderColor = frame == 0 ? celebrationColor : normalBorder;
            RenderBox(borderColor, palette.GetCelebrationFg().AnsiFg, displayMessage, boxLeft, boxTop, innerWidth);
            Thread.Sleep(FlashFrameMs);
        }

        // === Phase 2: Sparkle (400ms) — 4 frames x 100ms ===
        for (var frame = 0; frame < 4; frame++)
        {
            if (DimensionsChanged(terminalWidth, terminalHeight))
            {
                return;
            }

            // Each frame adds 2-3 sparkles at random positions
            var sparkleCount = rng.Next(2, 4); // 2 or 3
            for (var s = 0; s < sparkleCount; s++)
            {
                var row = rng.Next(sparkleTop, sparkleBottom + 1);
                var col = rng.Next(sparkleLeft, sparkleRight + 1);

                // Avoid placing sparkles on top of the box itself
                if (row >= boxTop && row <= boxTop + 2 && col >= boxLeft && col < boxLeft + boxWidth)
                {
                    continue;
                }

                var sparkleChar = rng.Next(2) == 0 ? SparkleA : SparkleB;
                lock (ConsoleSync.Lock)
                {
                    try
                    {
                        Console.SetCursorPosition(col, row);
                        Console.Write($"{celebrationColor}{sparkleChar}{Reset}");
                    }
                    catch
                    {
                        // Ignore console errors
                    }
                }

                sparklePositions.Add((row, col));
            }

            Thread.Sleep(SparkleFrameMs);
        }

        // === Phase 3: Typewriter (600ms) — character by character ===
        PlayTypewriterPhase(celebrationColor, displayMessage, boxLeft, boxTop, innerWidth, terminalWidth, terminalHeight);

        // === Phase 4: Settle (400ms) — 4 frames x 100ms, removing sparkles ===
        PlaySettlePhase(sparklePositions, terminalWidth, terminalHeight);
    }

    /// <summary>
    /// Builds the celebration message string.
    /// </summary>
    /// <param name="chapters">Number of chapters in the podcast.</param>
    /// <param name="duration">Total duration of the podcast.</param>
    /// <returns>Formatted message string.</returns>
    public static string BuildMessage(int chapters, TimeSpan duration)
    {
        var durationText = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{(int)duration.TotalMinutes}m {duration.Seconds}s";

        return $"\u2726 Podcast ready! {chapters} chapters \u00b7 {durationText}";
    }

    /// <summary>
    /// workspace-khpe.1: true when the live terminal no longer matches the
    /// dimensions the animation's coordinates were computed from. Redirected /
    /// dumb terminals report unchanged (the per-write guards absorb the rest).
    /// </summary>
    private static bool DimensionsChanged(int width, int height)
    {
        try
        {
            return Console.WindowWidth != width || Console.WindowHeight != height;
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void PlayTypewriterPhase(
        string celebrationColor,
        string displayMessage,
        int boxLeft,
        int boxTop,
        int innerWidth,
        int terminalWidth,
        int terminalHeight)
    {
        var msgRow = boxTop + 1;
        var msgStartCol = boxLeft + 1 + Math.Max(0, (innerWidth - displayMessage.Length) / 2);

        // Clear the message area first
        lock (ConsoleSync.Lock)
        {
            try
            {
                Console.SetCursorPosition(boxLeft + 1, msgRow);
                Console.Write(new string(' ', innerWidth));
            }
            catch
            {
                // Ignore console errors
            }
        }

        var delayPerChar = Math.Max(1, 600 / Math.Max(1, displayMessage.Length));

        for (var i = 0; i < displayMessage.Length; i++)
        {
            if (DimensionsChanged(terminalWidth, terminalHeight))
            {
                return;
            }

            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(msgStartCol + i, msgRow);
                    Console.Write($"{celebrationColor}{displayMessage[i]}{Reset}");
                }
                catch
                {
                    // Ignore console errors
                }
            }

            Thread.Sleep(delayPerChar);
        }
    }

    private static void PlaySettlePhase(List<(int Row, int Col)> sparklePositions, int terminalWidth, int terminalHeight)
    {
        // Distribute sparkles across the 4 frames for gradual removal
        var perFrame = Math.Max(1, (sparklePositions.Count + 3) / 4);

        for (var frame = 0; frame < 4; frame++)
        {
            if (DimensionsChanged(terminalWidth, terminalHeight))
            {
                return;
            }

            var start = frame * perFrame;
            var end = Math.Min(start + perFrame, sparklePositions.Count);

            for (var i = start; i < end; i++)
            {
                var (row, col) = sparklePositions[i];
                lock (ConsoleSync.Lock)
                {
                    try
                    {
                        Console.SetCursorPosition(col, row);
                        Console.Write(' ');
                    }
                    catch
                    {
                        // Ignore console errors
                    }
                }
            }

            Thread.Sleep(SettleFrameMs);
        }
    }

    private static void RenderBox(
        string borderColor,
        string textColor,
        string message,
        int left,
        int top,
        int innerWidth)
    {
        var centeredMessage = message.Length >= innerWidth
            ? message
            : message.PadLeft((innerWidth + message.Length) / 2).PadRight(innerWidth);

        lock (ConsoleSync.Lock)
        {
            try
            {
                // Top border
                Console.SetCursorPosition(left, top);
                Console.Write($"{borderColor}\u256d{new string('\u2500', innerWidth)}\u256e{Reset}");

                // Message line
                Console.SetCursorPosition(left, top + 1);
                Console.Write($"{borderColor}\u2502{textColor}{centeredMessage}{borderColor}\u2502{Reset}");

                // Bottom border
                Console.SetCursorPosition(left, top + 2);
                Console.Write($"{borderColor}\u2570{new string('\u2500', innerWidth)}\u256f{Reset}");
            }
            catch
            {
                // Ignore console errors
            }
        }
    }
}
