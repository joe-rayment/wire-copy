// Educational and personal use only.

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared rendering state and utility methods used by all sub-renderers.
/// </summary>
internal class RenderHelpers
{
    private int _linesWritten;

    public int LinesWritten => _linesWritten;

    public void Clear()
    {
        try
        {
            Console.SetCursorPosition(0, 0);
            _linesWritten = 0;
        }
        catch
        {
            try
            {
                Console.Clear();
            }
            catch
            {
                for (var i = 0; i < Console.WindowHeight; i++)
                {
                    Console.WriteLine();
                }
            }
        }
    }

    public void ClearRemainingLines()
    {
        try
        {
            var height = Console.WindowHeight;

            while (_linesWritten < height)
            {
                Console.SetCursorPosition(0, _linesWritten);
                Console.Write("\x1b[K");
                _linesWritten++;
            }
        }
        catch
        {
            // Ignore errors in non-standard console environments
        }
    }

    public void WriteLine(string text = "")
    {
        try
        {
            if (_linesWritten >= Console.WindowHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);
            Console.Write(text);
            Console.Write("\x1b[K");
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    public void WriteLineWithHighlight(string text, string searchQuery)
    {
        try
        {
            if (_linesWritten >= Console.WindowHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);

            var index = 0;
            var savedColor = Console.ForegroundColor;

            while (index < text.Length)
            {
                var matchPos = text.IndexOf(searchQuery, index, StringComparison.OrdinalIgnoreCase);
                if (matchPos < 0)
                {
                    Console.Write(text.Substring(index));
                    index = text.Length;
                }
                else
                {
                    if (matchPos > index)
                    {
                        Console.Write(text.Substring(index, matchPos - index));
                    }

                    Console.BackgroundColor = ConsoleColor.DarkYellow;
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.Write(text.Substring(matchPos, searchQuery.Length));
                    Console.ResetColor();
                    Console.ForegroundColor = savedColor;

                    index = matchPos + searchQuery.Length;
                }
            }

            Console.Write("\x1b[K");
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    public void WriteLineWithFocusHighlight(string text, bool use256Colors)
    {
        try
        {
            if (_linesWritten >= Console.WindowHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);

            if (use256Colors)
            {
                Console.Write($"{Colors.Bg256Highlight}{Colors.Fg256White}{text}{Colors.Reset}");
            }
            else
            {
                Console.Write($"{Colors.Bold}{text}{Colors.Reset}");
            }

            Console.Write("\x1b[K");
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    public static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (maxLength <= 3)
        {
            return text.Length <= maxLength ? text : text.Substring(0, Math.Max(0, maxLength));
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength - 3) + "...";
    }

    public static string TruncateUrl(string url, int maxLength)
    {
        if (string.IsNullOrEmpty(url) || url.Length <= maxLength)
        {
            return url;
        }

        var halfLen = (maxLength - 3) / 2;
        return url.Substring(0, halfLen) + "..." + url.Substring(url.Length - halfLen);
    }

    public static List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = string.Empty;

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxWidth)
            {
                if (!string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(currentLine);
                }

                currentLine = word;
            }
            else
            {
                currentLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
        {
            lines.Add(currentLine);
        }

        return lines;
    }

    /// <summary>
    /// Color palette constants with 256-color and 16-color fallbacks.
    /// </summary>
    internal static class Colors
    {
        public const string Reset = "\x1b[0m";
        public const string Bold = "\x1b[1m";

        // Foreground 256-color
        public const string Fg256White = "\x1b[38;5;252m";
        public const string Fg256Gray = "\x1b[38;5;245m";
        public const string Fg256DarkGray = "\x1b[38;5;240m";
        public const string Fg256Cyan = "\x1b[38;5;80m";
        public const string Fg256Green = "\x1b[38;5;114m";
        public const string Fg256Yellow = "\x1b[38;5;220m";
        public const string Fg256Red = "\x1b[38;5;203m";

        // Background 256-color
        public const string Bg256Highlight = "\x1b[48;5;235m";
        public const string Bg256Selection = "\x1b[48;5;237m";

        // 16-color fallbacks
        public const string Fg16White = "\x1b[37m";
        public const string Fg16Gray = "\x1b[90m";
        public const string Fg16Cyan = "\x1b[36m";
        public const string Fg16Green = "\x1b[32m";
        public const string Fg16Yellow = "\x1b[33m";
        public const string Fg16Red = "\x1b[31m";

        public static string Fg(bool use256, string color256, string color16) => use256 ? color256 : color16;

        public static string Bg(bool use256, string color256, string color16) => use256 ? color256 : color16;
    }
}
