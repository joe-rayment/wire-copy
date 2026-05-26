// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Animations;

/// <summary>
/// Looping eighth-block breathing pulse used during bot-challenge / login waits.
/// The bar's cells share a single fill level that cycles through
/// <c>▏▎▍▌▋▊▉█</c> and back over ~3.6s, signalling "the app is still waiting on
/// you" without spending input cycles. Start returns a handle whose Dispose
/// stops the loop and clears the row.
/// </summary>
internal static class BreathingBarAnimation
{
    private const string Reset = "\x1b[0m";
    private const int FrameDelayMs = 225; // 16 frames × 225ms ≈ 3.6s per cycle

    // Eighth-block grow sequence (U+258F .. U+2588). Includes the space at frame 0
    // so the bar fully clears between cycles — without it the loop looks like a
    // ratchet, not a breath.
    private static readonly char[] GrowFrames = [' ', '▏', '▎', '▍', '▌', '▋', '▊', '▉', '█'];

    /// <summary>
    /// Starts the breathing-bar loop on a background thread. The bar renders
    /// <paramref name="width"/> cells wide at (<paramref name="row"/>, <paramref name="col"/>)
    /// using <see cref="ThemePalette.GetAccentFg"/>. Dispose the returned handle
    /// to stop the loop and clear the row.
    /// </summary>
    /// <param name="row">Console row for the bar.</param>
    /// <param name="col">Console column where the bar begins.</param>
    /// <param name="width">Number of cells the bar occupies (recommended 4–6).</param>
    /// <param name="palette">Theme palette for the bar's foreground color.</param>
    public static IDisposable Start(int row, int col, int width, ThemePalette palette)
    {
        if (width <= 0 || Console.IsOutputRedirected)
        {
            return NoOpHandle.Instance;
        }

        var color = palette.GetAccentFg().AnsiFg;
        return new BreathingBarHandle(row, col, width, color);
    }

    /// <summary>
    /// Convenience overload that places the bar two rows below the centered
    /// human-action box rendered by <c>TerminalPageRenderer.RenderHumanAction</c>.
    /// Mirrors that renderer's <c>topPad = (h-boxHeight)/3</c> placement so the
    /// bar consistently lands in the open space beneath the box.
    /// </summary>
    public static IDisposable StartForBotChallenge(ThemePalette palette)
    {
        const int BoxHeight = 11; // 9 content lines + 2 borders, see RenderHumanAction
        const int BarWidth = 5;

        int termWidth;
        int termHeight;
        try
        {
            termWidth = Console.WindowWidth;
            termHeight = Console.WindowHeight;
        }
        catch
        {
            return NoOpHandle.Instance;
        }

        if (termWidth < BarWidth + 4 || termHeight < BoxHeight + 3)
        {
            return NoOpHandle.Instance;
        }

        var topPad = Math.Max(0, (termHeight - BoxHeight) / 3);
        var row = Math.Min(termHeight - 1, topPad + BoxHeight + 1);
        var col = Math.Max(0, (termWidth - BarWidth) / 2);

        return Start(row, col, BarWidth, palette);
    }

    private sealed class BreathingBarHandle : IDisposable
    {
        private readonly int _row;
        private readonly int _col;
        private readonly int _width;
        private readonly string _color;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private int _disposed; // 0 = running, 1 = disposed

        public BreathingBarHandle(int row, int col, int width, string color)
        {
            _row = row;
            _col = col;
            _width = width;
            _color = color;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "BreathingBar" };
            _thread.Start();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cts.Cancel();
            try
            {
                _thread.Join(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Best-effort join.
            }

            // Clear the row the bar occupied.
            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(_col, _row);
                    Console.Write(new string(' ', _width));
                }
                catch
                {
                    // Non-standard console — leave as-is.
                }
            }

            _cts.Dispose();
        }

        private void RunLoop()
        {
            // Grow 0→8 then shrink 7→0 = 16 unique frames per loop.
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    for (var i = 0; i < GrowFrames.Length; i++)
                    {
                        DrawFrame(GrowFrames[i]);
                        if (_cts.Token.WaitHandle.WaitOne(FrameDelayMs))
                        {
                            return;
                        }
                    }

                    for (var i = GrowFrames.Length - 2; i > 0; i--)
                    {
                        DrawFrame(GrowFrames[i]);
                        if (_cts.Token.WaitHandle.WaitOne(FrameDelayMs))
                        {
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Swallow — animation must never escape onto a wait loop.
            }
        }

        private void DrawFrame(char fill)
        {
            var line = new string(fill, _width);
            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(_col, _row);
                    Console.Write($"{_color}{line}{Reset}");
                }
                catch
                {
                    // Non-standard console — skip frame.
                }
            }
        }
    }

    private sealed class NoOpHandle : IDisposable
    {
        public static readonly NoOpHandle Instance = new();

        public void Dispose()
        {
        }
    }
}
