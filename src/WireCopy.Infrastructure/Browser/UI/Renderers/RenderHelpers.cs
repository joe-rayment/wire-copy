// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared rendering state and utility methods used by all sub-renderers.
/// </summary>
internal class RenderHelpers
{
    /// <summary>
    /// Diagnostic hook for workspace-1f5a (cursor highlight drops under rapid input).
    /// When the WIRECOPY_DEBUG_RENDER_FRAMES env var is set to a path, every line
    /// emitted via WriteLine* is appended to that file with frame metadata so frames
    /// can be diffed offline by the user on their own terminal.
    /// </summary>
    private static readonly string? DebugFramePath =
        Environment.GetEnvironmentVariable("WIRECOPY_DEBUG_RENDER_FRAMES");

    private static readonly object DebugFrameLock = new();
    private static long _debugFrameSeq;

    /// <summary>
    /// Frame buffer for atomic single-write rendering (workspace-1f5a).
    /// When non-null, all Console.Write/SetCursorPosition calls are routed
    /// through this StringBuilder so the entire frame emits as a single
    /// Console.Out.Write at EndFrame. This prevents the OS pipe buffer from
    /// flushing mid-escape and splitting an SGR sequence so the terminal
    /// receives only part of it (which causes the cursor highlight to drop).
    /// Opt-in via BeginFrame; null means legacy direct-write mode (used by
    /// tests that capture Console.Out).
    /// </summary>
    private StringBuilder? _frameBuffer;

    private int _linesWritten;
    private int _terminalHeight;

    public int LinesWritten => _linesWritten;

    public int TerminalHeight
    {
        get => _terminalHeight > 0 ? _terminalHeight : Console.WindowHeight;
        set => _terminalHeight = value;
    }

    /// <summary>
    /// Left margin in columns for centering content (e.g., reader view).
    /// </summary>
    public int LeftMargin { get; set; }

    /// <summary>
    /// Global left column offset applied to every WriteLine* line ON TOP of
    /// <see cref="LeftMargin"/> (workspace-8fkv). Non-zero only when the headed browser is
    /// docked to the LEFT: it pushes the app's whole frame into the uncovered right columns
    /// while each line's full-width <c>\x1b[K</c> blanks the left columns the browser covers.
    /// Reset to zero by <see cref="Clear"/> so a dock offset never leaks into a full-screen
    /// view (loading/error boxes) that did not opt in.
    /// </summary>
    public int ColumnOffset { get; set; }

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

        if (maxWidth <= 1)
        {
            return GetDisplayWidth(text) <= maxWidth ? text : TruncateToWidth(text, maxWidth);
        }

        if (GetDisplayWidth(text) <= maxWidth)
        {
            return text;
        }

        return TruncateToWidth(text, maxWidth - 1) + "\u2026";
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

        var halfWidth = (maxWidth - 1) / 2;
        var prefix = TruncateToWidth(url, halfWidth);
        var suffix = TakeTrailingWidth(url, halfWidth);
        return $"{prefix}\u2026{suffix}";
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

    /// <summary>
    /// Starts a buffered render frame. All subsequent Write/SetCursorPosition
    /// calls accumulate into a single StringBuilder until EndFrame is called,
    /// at which point the entire frame is emitted via a single Console.Out.Write
    /// followed by Console.Out.Flush. Atomic from the terminal's perspective —
    /// escape sequences cannot be split mid-write by OS pipe buffer flushes
    /// (workspace-1f5a).
    /// </summary>
    public void BeginFrame()
    {
        _frameBuffer = new StringBuilder(8192);
        _linesWritten = 0;
    }

    /// <summary>
    /// Emits the buffered frame as a single atomic write and clears buffering.
    /// Safe to call when BeginFrame was not invoked (no-op). The write is framed
    /// in DEC private mode 2026 (synchronized update, workspace-tj1z.6): the
    /// terminal holds presentation until the reset, so a repaint can never tear
    /// mid-frame. Emitted unconditionally — non-supporting terminals ignore the
    /// DECSET per spec; xterm.js 6 implements it (with a 1s safety auto-flush),
    /// as do iTerm2/kitty/wezterm.
    /// </summary>
    public void EndFrame()
    {
        if (_frameBuffer == null)
        {
            return;
        }

        try
        {
            Console.Out.Write("\x1b[?2026h" + _frameBuffer.ToString() + "\x1b[?2026l");
            Console.Out.Flush();
        }
        catch
        {
            // Swallow — terminal may be in a bad state mid-shutdown.
        }

        _frameBuffer = null;
    }

    public void Clear()
    {
        // Per-frame reset: the dock offset is opted into by the side-by-side views after
        // Clear(), so zeroing it here keeps it from leaking into a full-screen takeover.
        ColumnOffset = 0;
        try
        {
            EmitCursorPos(0, 0);
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

    /// <summary>
    /// Renders a full-width horizontal rule to mark end of content.
    /// Uses DimFg from the theme palette — visible but not competing with content.
    /// </summary>
    public void RenderEndOfContentRule(ThemePalette palette, int terminalWidth)
    {
        var ruleWidth = Math.Max(1, terminalWidth - 2);
        var dimFg = palette.GetDimFg().AnsiFg;
        WriteLine($" {dimFg}{new string('\u2500', ruleWidth)}\x1b[0m");
    }

    /// <summary>
    /// Clears the gap between current content and the 2-line status bar,
    /// then positions the cursor at TerminalHeight-2 so the next WriteLines
    /// render the separator + content at the bottom of the screen.
    /// </summary>
    public void PositionAtBottom()
    {
        try
        {
            var targetLine = TerminalHeight - 2;
            if (_linesWritten < targetLine)
            {
                var sb = new StringBuilder((targetLine - _linesWritten) * 10);
                for (var line = _linesWritten; line < targetLine; line++)
                {
                    sb.Append($"\x1b[{line + 1};1H\x1b[K");
                }

                EmitWrite(sb.ToString());
                _linesWritten = targetLine;
            }
        }
        catch
        {
            // Ignore errors in non-standard console environments
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

            EmitWrite(sb.ToString());
            _linesWritten = height;
        }
        catch
        {
            // Ignore errors in non-standard console environments
        }
    }

    /// <summary>
    /// Positions the cursor at (col,row) and writes the given text. When a frame
    /// buffer is active, both the cursor position escape and the text accumulate
    /// into the buffer so the toast/overlay survives the EndFrame flush. When no
    /// frame is active (legacy mode), falls back to direct Console writes guarded
    /// by <see cref="ConsoleSync.Lock"/>.
    /// Used by overlay components (toasts) that need to position absolutely.
    /// </summary>
    public void WriteAt(int col, int row, string text)
    {
        if (_frameBuffer != null)
        {
            EmitCursorPos(col, row);
            EmitWrite(text);
            return;
        }

        lock (ConsoleSync.Lock)
        {
            try
            {
                Console.SetCursorPosition(col, row);
                Console.Write(text);
            }
            catch
            {
                // Non-standard console — silently skip
            }
        }
    }

    public void WriteLine(string text = "")
        => WriteLineCore(text, "WriteLine", text, () => EmitWrite(text));

    public void WriteLineWithHighlight(string text, string searchQuery, ThemePalette palette)
        => WriteLineCore(text, "WriteLineWithHighlight", text, () =>
            WriteSearchHighlightedContent(text, searchQuery, palette));

    public void WriteLineWithIndicator(string text, string indicatorAnsi, string? searchQuery, ThemePalette? palette)
        => WriteLineCore(text, "WriteLineWithIndicator", indicatorAnsi + text, () =>
        {
            // Write the indicator prefix
            EmitWrite(indicatorAnsi);

            // Strip leading space from content to maintain alignment since indicator takes its place
            var content = text.Length > 0 && text[0] == ' ' ? text.Substring(1) : text;

            if (!string.IsNullOrEmpty(searchQuery) && palette != null)
            {
                WriteSearchHighlightedContent(content, searchQuery, palette);
            }
            else
            {
                EmitWrite(content);
            }
        });

    /// <summary>
    /// Writes a line with a paragraph indicator and optional underline for the cursor line.
    /// The paragraph indicator occupies column 0. When isCursorLine is true, the entire
    /// line is underlined from the paragraph indicator to the right edge of the terminal.
    /// </summary>
    public void WriteLineWithDualIndicator(
        string text,
        bool isCursorLine,
        string? paragraphAnsi,
        string? searchQuery,
        ThemePalette? palette,
        int terminalWidth = 0,
        ThemeColor? cursorColor = null)
        => WriteLineCore(text, "WriteLineWithDualIndicator", text, () =>
        {
            var cursorFg = isCursorLine && cursorColor != null ? cursorColor.Value.AnsiFg : string.Empty;
            var cursorFgOff = isCursorLine && cursorColor != null ? "\x1b[0m" : string.Empty;

            // SGR 58 sets underline color independent of foreground, so the highlight
            // stripe stays one consistent color even when col 0 (paragraph indicator)
            // uses a different fg. SGR 59 resets underline color. Terminals that don't
            // support SGR 58 ignore it and fall back to fg-tinted underline.
            string underline;
            if (isCursorLine && cursorColor != null)
            {
                underline = $"\x1b[4m\x1b[58;5;{cursorColor.Value.AnsiCode}m";
            }
            else if (isCursorLine)
            {
                underline = "\x1b[4m";
            }
            else
            {
                underline = string.Empty;
            }

            string underlineOff;
            if (isCursorLine && cursorColor != null)
            {
                underlineOff = "\x1b[24m\x1b[59m";
            }
            else if (isCursorLine)
            {
                underlineOff = "\x1b[24m";
            }
            else
            {
                underlineOff = string.Empty;
            }

            // Column 0: paragraph indicator or original char.
            // When this is the cursor line, strip any embedded reset from the indicator
            // so the underline carries through.
            if (paragraphAnsi != null)
            {
                var indicator = isCursorLine ? paragraphAnsi.Replace("\x1b[0m", string.Empty) : paragraphAnsi;
                EmitWrite($"{underline}{indicator}{underlineOff}");
            }
            else
            {
                EmitWrite($"{underline}{cursorFg}{(text.Length > 0 ? text[0] : ' ')}{cursorFgOff}{underlineOff}");
            }

            // Remaining content (columns 1+)
            var content = text.Length > 1 ? text[1..] : string.Empty;

            if (isCursorLine)
            {
                EmitWrite($"{cursorFg}{underline}");
            }

            if (!string.IsNullOrEmpty(searchQuery) && palette != null)
            {
                WriteSearchHighlightedContent(content, searchQuery, palette);
            }
            else
            {
                EmitWrite(content);
            }

            // Extend underline to right edge of screen
            if (isCursorLine && terminalWidth > 0)
            {
                var textWidth = GetDisplayWidth(text);
                var remaining = terminalWidth - LeftMargin - textWidth;
                if (remaining > 0)
                {
                    EmitWrite(new string(' ', remaining));
                }

                EmitWrite("\x1b[24m\x1b[0m");
            }

            EmitWrite("\x1b[K");
        });

    public void WriteLineWithFocusHighlight(string text, ThemePalette palette)
        => WriteLineCore(text, "WriteLineWithFocusHighlight", text, () =>
        {
            EmitWrite($"{palette.SelectedItemBg.AnsiBg}{palette.SelectedItemFg.AnsiFg}{text}{AnsiCodes.Reset}");
            EmitWrite("\x1b[K");
        });

    /// <summary>
    /// Logs a rendered line to the debug frame file when WIRECOPY_DEBUG_RENDER_FRAMES is set.
    /// Diagnostic for workspace-1f5a — captures the exact escape sequence emitted for each
    /// frame so frame N (good) vs frame N+1 (broken) can be diffed offline.
    /// </summary>
    private static void LogDebugFrame(string source, int line, string text)
    {
        if (DebugFramePath == null)
        {
            return;
        }

        try
        {
            var seq = System.Threading.Interlocked.Increment(ref _debugFrameSeq);
            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            var hex = EscapeNonPrintable(text);
            var entry = $"{seq:D8} {ts} {source} L{line:D3} len={text.Length} {hex}\n";
            lock (DebugFrameLock)
            {
                File.AppendAllText(DebugFramePath, entry);
            }
        }
        catch
        {
            // Swallow — diagnostic must never break the renderer.
        }
    }

    private static string EscapeNonPrintable(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c == '\x1b')
            {
                sb.Append("\\e");
            }
            else if (c < 0x20 || c == 0x7f)
            {
                sb.Append('\\').Append('x').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
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

    /// <summary>
    /// Shared frame-line writer. Owns the viewport guard, cursor positioning,
    /// left-margin line-clear, debug-frame logging, line-count bookkeeping, and
    /// the non-TTY catch fallback that every WriteLine* variant repeats. Each
    /// variant supplies only its body (<paramref name="emitBody"/>) plus the text
    /// used for debug logging and the fallback Console.WriteLine.
    /// </summary>
    private void WriteLineCore(string fallbackText, string debugSource, string debugText, Action emitBody)
    {
        try
        {
            if (_linesWritten >= TerminalHeight)
            {
                return;
            }

            // Clear the full line first, then shift to the content origin. The full-width
            // \x1b[K blanks the columns a left-docked browser sits over (ColumnOffset) and
            // any reader-centering margin (LeftMargin); the shift starts content there.
            EmitCursorPos(0, _linesWritten);
            EmitWrite("\x1b[K");
            var startCol = ColumnOffset + LeftMargin;
            if (startCol > 0)
            {
                EmitCursorPos(startCol, _linesWritten);
            }

            emitBody();
            LogDebugFrame(debugSource, _linesWritten, debugText);
            _linesWritten++;
        }
        catch
        {
            Console.WriteLine(fallbackText);
            _linesWritten++;
        }
    }

    /// <summary>
    /// Writes a string either to the active frame buffer or directly to Console.
    /// Centralised so every escape sequence flows through the same gate
    /// (workspace-1f5a). When _frameBuffer is non-null, all output accumulates
    /// in the buffer until EndFrame emits it as one atomic Console.Out.Write.
    /// </summary>
    private void EmitWrite(string s)
    {
        if (_frameBuffer != null)
        {
            _frameBuffer.Append(s);
        }
        else
        {
            Console.Write(s);
        }
    }

    /// <summary>
    /// Positions the cursor at (col,row). When buffering, emits the ANSI CUP
    /// sequence into the frame buffer; otherwise calls Console.SetCursorPosition.
    /// </summary>
    private void EmitCursorPos(int col, int row)
    {
        if (_frameBuffer != null)
        {
            _frameBuffer.Append("\x1b[").Append(row + 1).Append(';').Append(col + 1).Append('H');
        }
        else
        {
            Console.SetCursorPosition(col, row);
        }
    }

    private void WriteSearchHighlightedContent(string text, string searchQuery, ThemePalette palette)
    {
        var index = 0;

        while (index < text.Length)
        {
            var matchPos = text.IndexOf(searchQuery, index, StringComparison.OrdinalIgnoreCase);
            if (matchPos < 0)
            {
                EmitWrite(text.Substring(index));
                index = text.Length;
            }
            else
            {
                if (matchPos > index)
                {
                    EmitWrite(text.Substring(index, matchPos - index));
                }

                EmitWrite($"{palette.SearchHighlightBg.AnsiBg}{palette.SearchHighlightFg.AnsiFg}");
                EmitWrite(text.Substring(matchPos, searchQuery.Length));
                EmitWrite(AnsiCodes.Reset);

                index = matchPos + searchQuery.Length;
            }
        }
    }

    /// <summary>
    /// Theme-independent ANSI control codes (reset, bold, etc.).
    /// These are standard terminal sequences, not colors — use ThemePalette for all color output.
    /// </summary>
    internal static class AnsiCodes
    {
        public const string Reset = "\x1b[0m";
        public const string Bold = "\x1b[1m";
    }
}
