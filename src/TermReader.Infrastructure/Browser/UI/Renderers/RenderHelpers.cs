// Educational and personal use only.

using System.Globalization;
using System.Text;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared rendering state and utility methods used by all sub-renderers.
/// </summary>
internal class RenderHelpers
{
    private int _linesWritten;
    private int _terminalHeight;

    public int LinesWritten => _linesWritten;

    public int TerminalHeight
    {
        get => _terminalHeight > 0 ? _terminalHeight : Console.WindowHeight;
        set => _terminalHeight = value;
    }

    /// <summary>
    /// Returns the display width of a character, accounting for East Asian wide characters.
    /// CJK characters and most emoji occupy 2 columns in a terminal.
    /// </summary>
    public static int GetCharDisplayWidth(char c)
    {
        if (c < 0x1100)
        {
            return 1;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category == UnicodeCategory.NonSpacingMark ||
            category == UnicodeCategory.EnclosingMark ||
            category == UnicodeCategory.Format)
        {
            return 0;
        }

        // East Asian wide characters: CJK Unified, Hangul, Katakana/Hiragana, fullwidth forms.
        // Ranges from Unicode East Asian Width property (W and F categories).
        if (c <= 0x115F ||
            c == 0x2329 || c == 0x232A ||
            (c >= 0x2E80 && c <= 0x303E) ||
            (c >= 0x3040 && c <= 0x33BF) ||
            (c >= 0x3400 && c <= 0x4DBF) ||
            (c >= 0x4E00 && c <= 0xA4CF) ||
            (c >= 0xA960 && c <= 0xA97C) ||
            (c >= 0xAC00 && c <= 0xD7FF) ||
            (c >= 0xF900 && c <= 0xFAFF) ||
            (c >= 0xFE10 && c <= 0xFE6F) ||
            (c >= 0xFF01 && c <= 0xFF60) ||
            (c >= 0xFFE0 && c <= 0xFFE6))
        {
            return 2;
        }

        return 1;
    }

    /// <summary>
    /// Returns the display width of a string in terminal columns.
    /// </summary>
    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return GetDisplayWidthCore(text.AsSpan());
    }

    public static string TruncateText(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (maxWidth <= 3)
        {
            return GetDisplayWidth(text) <= maxWidth ? text : TruncateToWidth(text, maxWidth);
        }

        if (GetDisplayWidth(text) <= maxWidth)
        {
            return text;
        }

        return TruncateToWidth(text, maxWidth - 3) + "...";
    }

    public static string FormatCacheAge(DateTime? cachedAt)
    {
        if (cachedAt == null)
        {
            return "just now";
        }

        var age = DateTime.UtcNow - cachedAt.Value;
        return age.TotalMinutes switch
        {
            < 1 => "<1m ago",
            < 60 => $"{(int)age.TotalMinutes}m ago",
            _ => $"{(int)age.TotalHours}h ago"
        };
    }

    public static string TruncateUrl(string url, int maxWidth)
    {
        if (string.IsNullOrEmpty(url) || GetDisplayWidth(url) <= maxWidth)
        {
            return url;
        }

        var halfWidth = (maxWidth - 3) / 2;
        var prefix = TruncateToWidth(url, halfWidth);
        var suffix = TakeTrailingWidth(url, halfWidth);
        return $"{prefix}...{suffix}";
    }

    public static List<string> WrapText(string text, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return new List<string>();
        }

        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = new StringBuilder();
        var currentWidth = 0;

        foreach (var word in words)
        {
            var wordWidth = GetDisplayWidth(word);

            if (wordWidth > maxWidth)
            {
                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentWidth = 0;
                }

                // Break long word across lines
                var sb = new StringBuilder();
                var w = 0;
                foreach (var c in word)
                {
                    var cw = GetCharDisplayWidth(c);
                    if (w + cw > maxWidth)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear();
                        w = 0;
                    }

                    sb.Append(c);
                    w += cw;
                }

                if (sb.Length > 0)
                {
                    currentLine.Append(sb);
                    currentWidth = w;
                }
            }
            else if (currentWidth + wordWidth + (currentLine.Length > 0 ? 1 : 0) > maxWidth)
            {
                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                }

                currentLine.Append(word);
                currentWidth = wordWidth;
            }
            else
            {
                if (currentLine.Length > 0)
                {
                    currentLine.Append(' ');
                    currentWidth++;
                }

                currentLine.Append(word);
                currentWidth += wordWidth;
            }
        }

        if (currentLine.Length > 0)
        {
            lines.Add(currentLine.ToString());
        }

        return lines;
    }

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
                for (var i = 0; i < TerminalHeight; i++)
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
            var height = TerminalHeight;
            var remaining = height - _linesWritten;

            if (remaining <= 0)
            {
                return;
            }

            // Batch all clear operations into a single write
            var sb = new StringBuilder(remaining * 10);
            for (var line = _linesWritten; line < height; line++)
            {
                sb.Append($"\x1b[{line + 1};1H\x1b[K");
            }

            Console.Write(sb.ToString());
            _linesWritten = height;
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
            if (_linesWritten >= TerminalHeight)
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

    public void WriteLineWithHighlight(string text, string searchQuery, ThemePalette palette)
    {
        try
        {
            if (_linesWritten >= TerminalHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);
            WriteSearchHighlightedContent(text, searchQuery, palette);
            Console.Write("\x1b[K");
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    public void WriteLineWithIndicator(string text, string indicatorAnsi, string? searchQuery, ThemePalette? palette)
    {
        try
        {
            if (_linesWritten >= TerminalHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);

            // Write the indicator prefix
            Console.Write(indicatorAnsi);

            // Strip leading space from content to maintain alignment since indicator takes its place
            var content = text.Length > 0 && text[0] == ' ' ? text.Substring(1) : text;

            if (!string.IsNullOrEmpty(searchQuery) && palette != null)
            {
                WriteSearchHighlightedContent(content, searchQuery, palette);
            }
            else
            {
                Console.Write(content);
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

    public void WriteLineWithFocusHighlight(string text, ThemePalette palette)
    {
        try
        {
            if (_linesWritten >= TerminalHeight)
            {
                return;
            }

            Console.SetCursorPosition(0, _linesWritten);

            Console.Write($"{palette.SelectedItemBg.AnsiBg}{palette.SelectedItemFg.AnsiFg}{text}{Colors.Reset}");

            Console.Write("\x1b[K");
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(text);
            _linesWritten++;
        }
    }

    private static int GetDisplayWidthCore(ReadOnlySpan<char> span)
    {
        var width = 0;
        var i = 0;
        while (i < span.Length)
        {
            var c = span[i];

            // Skip ANSI escape sequences (\x1b[...m)
            if (c == '\x1b' && i + 1 < span.Length && span[i + 1] == '[')
            {
                i += 2;
                while (i < span.Length && span[i] != 'm')
                {
                    i++;
                }

                i++;
                continue;
            }

            // Surrogate pairs (emoji) — count as 2 columns
            if (char.IsHighSurrogate(c) && i + 1 < span.Length && char.IsLowSurrogate(span[i + 1]))
            {
                width += 2;
                i += 2;
                continue;
            }

            width += GetCharDisplayWidth(c);
            i++;
        }

        return width;
    }

    private static string TruncateToWidth(string text, int maxWidth)
    {
        var sb = new StringBuilder();
        var width = 0;
        foreach (var c in text)
        {
            var cw = GetCharDisplayWidth(c);
            if (width + cw > maxWidth)
            {
                break;
            }

            sb.Append(c);
            width += cw;
        }

        return sb.ToString();
    }

    private static string TakeTrailingWidth(string text, int maxWidth)
    {
        var totalWidth = 0;
        var startIndex = text.Length;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var cw = GetCharDisplayWidth(text[i]);
            if (totalWidth + cw > maxWidth)
            {
                break;
            }

            totalWidth += cw;
            startIndex = i;
        }

        return text[startIndex..];
    }

    private static void WriteSearchHighlightedContent(string text, string searchQuery, ThemePalette palette)
    {
        var index = 0;

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

                Console.Write($"{palette.SearchHighlightBg.AnsiBg}{palette.SearchHighlightFg.AnsiFg}");
                Console.Write(text.Substring(matchPos, searchQuery.Length));
                Console.Write(Colors.Reset);

                index = matchPos + searchQuery.Length;
            }
        }
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
