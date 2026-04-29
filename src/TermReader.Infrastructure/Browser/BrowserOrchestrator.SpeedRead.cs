// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI;

namespace TermReader.Infrastructure.Browser;

public partial class BrowserOrchestrator
{
    /// <summary>
    /// Default rendering overhead estimate (ms) used on the first speed-read line
    /// before a real measurement is available. Subsequent lines pass the actual
    /// measured render time from the previous tick instead of this constant.
    /// </summary>
    internal const int DefaultRenderOverheadMs = 100;

    /// <summary>
    /// Measured wall-clock duration (ms) of the most recent speed-read render
    /// (advance cursor + RenderCurrentPageAsync). Subtracted from the next
    /// line's computed delay so the timer-fire → render → next-timer cycle
    /// equals the WPM-implied duration. Reset to <see cref="DefaultRenderOverheadMs"/>
    /// whenever speed reading is (re)started.
    /// </summary>
    private int _lastLineRenderMs = DefaultRenderOverheadMs;

    /// <summary>
    /// Computes the delay in milliseconds for a given line during speed reading.
    /// Delegates to the line cache to find line content and next-line context.
    /// Uses the most recent measured render overhead so effective WPM stays accurate.
    /// </summary>
    private int ComputeLineDelayMs(int lineIndex, int wpm)
    {
        var lines = _lineCacheManager.CachedLines;
        if (lines == null || lines.Count == 0 || lineIndex < 0 || lineIndex >= lines.Count)
        {
            return 300;
        }

        var nextLineBlank = lineIndex + 1 < lines.Count && string.IsNullOrEmpty(lines[lineIndex + 1]);
        return ComputeLineDelayMs(lines[lineIndex], wpm, nextLineBlank, _lastLineRenderMs);
    }

    /// <summary>
    /// Pure computation: delay in ms for a line based on word count and WPM.
    /// If nextLineBlank (paragraph boundary), adds a 150ms pause.
    /// Subtracts the supplied <paramref name="renderOverheadMs"/> (the actual
    /// measured render time of the previous tick, or <see cref="DefaultRenderOverheadMs"/>
    /// on the first tick) so total cycle time matches the configured WPM.
    /// Minimum 30ms floor.
    /// </summary>
#pragma warning disable SA1202 // Internal test helper placed near its private caller
    internal static int ComputeLineDelayMs(
        string line,
        int wpm,
        bool nextLineBlank,
        int renderOverheadMs = DefaultRenderOverheadMs)
    {
        var wordCount = CountWordsStrippingAnsi(line);
        if (wordCount == 0)
        {
            return 30;
        }

        var msPerWord = 60000.0 / wpm;
        var delayMs = (int)(wordCount * msPerWord);

        if (nextLineBlank)
        {
            delayMs += 150;
        }

        // Subtract measured (or default-estimated) rendering overhead so that
        // the full timer-fire → render → next-timer cycle equals the WPM-implied
        // duration. Without this, every line drifts slow by the render time.
        var overhead = Math.Max(0, renderOverheadMs);
        return Math.Max(30, delayMs - overhead);
    }

    /// <summary>
    /// Advances the speed reading cursor one line forward, skipping blank lines.
    /// Paragraph pauses are handled by ComputeLineDelayMs adding extra delay
    /// when the current line precedes a blank. Returns false if at end of article.
    /// </summary>
    private bool AdvanceSpeedReadCursor(RenderOptions options)
    {
        _lineCacheManager.EnsureLineCache(options);
        var lines = _lineCacheManager.CachedLines;
        if (lines == null || lines.Count == 0)
        {
            return false;
        }

        var cursor = _navigationService.ReaderCursorLine;
        var totalLines = lines.Count;

        var newCursor = cursor + 1;

        // Skip blank lines
        while (newCursor < totalLines && string.IsNullOrEmpty(lines[newCursor]))
        {
            newCursor++;
        }

        if (newCursor >= totalLines)
        {
            return false; // End of article
        }

        _navigationService.SetReaderCursorLine(newCursor);

        // Scroll only when the cursor reaches the very last line of the viewport,
        // then jump a full page to minimize visual movement during speed reading.
        var scroll = _navigationService.CurrentContext.ScrollOffset;
        var vpHeight = GetReaderViewportHeight(options);
        var maxScroll = Math.Max(0, totalLines - vpHeight);

        if (newCursor >= scroll + vpHeight)
        {
            // Jump a full viewport (cursor lands at top of new page)
            scroll = Math.Min(maxScroll, newCursor);
            _navigationService.SetScrollOffset(Math.Clamp(scroll, 0, maxScroll));
        }

        return true;
    }

    internal static int CountWordsStrippingAnsi(string text)
    {
        var wordCount = 0;
        var inWord = false;
        var i = 0;
        while (i < text.Length)
        {
            // Skip ANSI escape sequences
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                i += 2;
                while (i < text.Length && text[i] != 'm')
                {
                    i++;
                }

                if (i < text.Length)
                {
                    i++; // skip 'm'
                }

                continue;
            }

            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                wordCount++;
            }

            i++;
        }

        return wordCount;
    }
#pragma warning restore SA1202
}
