// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;

namespace WireCopy.Infrastructure.Browser;

public partial class BrowserOrchestrator
{
    /// <summary>
    /// Default rendering overhead estimate (ms) used on the first speed-read line
    /// before a real measurement is available. Subsequent lines pass the actual
    /// measured render time from the previous tick instead of this constant.
    /// </summary>
    internal const int DefaultRenderOverheadMs = 100;

    // workspace-opnh: opt-in diagnostic for the reported 5-10s speed-read startup delay.
    // Static analysis (incl. this session) found NO multi-second op on the activation path
    // — the line cache is eagerly built at the Readable render, WrapText is O(n), input is
    // picked up promptly, and the first tick is ~30ms — so the delay needs a live capture.
    // Set WIRECOPY_DEBUG_SPEEDREAD_TIMING to log (a) the activation-render duration and
    // (b) activation→first-cursor-advance wall-clock. Read once; zero cost when unset
    // (mirrors the WIRECOPY_DEBUG_RENDER_FRAMES pattern).
    private static readonly bool SpeedReadTimingEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIRECOPY_DEBUG_SPEEDREAD_TIMING"));

    /// <summary>
    /// Measured wall-clock duration (ms) of the most recent speed-read render
    /// (advance cursor + RenderCurrentPageAsync). Subtracted from the next
    /// line's computed delay so the timer-fire → render → next-timer cycle
    /// equals the WPM-implied duration. Reset to <see cref="DefaultRenderOverheadMs"/>
    /// whenever speed reading is (re)started.
    /// </summary>
    private int _lastLineRenderMs = DefaultRenderOverheadMs;

    // workspace-opnh: started when speed-read is activated; consumed on the first cursor
    // advance to report activation→first-advance wall-clock (only when SpeedReadTimingEnabled).
    private System.Diagnostics.Stopwatch? _speedReadActivationSw;

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

        // workspace-1m3h.4: the cache now ends with the end-of-article footer
        // (— end — / stats lines). Speed reading must finish at the last CONTENT
        // line rather than stepping through and "reading" the footer.
        var contentEnd = _lineCacheManager.ContentLineCount > 0
            ? Math.Min(_lineCacheManager.ContentLineCount, totalLines)
            : totalLines;

        var newCursor = cursor + 1;

        // Skip blank lines
        while (newCursor < contentEnd && string.IsNullOrEmpty(lines[newCursor]))
        {
            newCursor++;
        }

        if (newCursor >= contentEnd)
        {
            return false; // End of article
        }

        _navigationService.SetReaderCursorLine(newCursor);

        // workspace-umi7: vpHeight MUST come from the same ReaderLayout helper
        // the renderer uses (see GetReaderViewportHeight). ScrollForCursor is a
        // pure function so unit tests can lock the invariant "cursor stays
        // inside the rendered viewport" across many sizes without standing up
        // the full app.
        var scroll = _navigationService.CurrentContext.ScrollOffset;
        var vpHeight = GetReaderViewportHeight(options);
        var newScroll = ScrollForCursor(newCursor, scroll, vpHeight, totalLines);
        if (newScroll != scroll)
        {
            _navigationService.SetScrollOffset(newScroll);
        }

        return true;
    }

    /// <summary>
    /// Pure scroll-offset calculation for the speed-read cursor (workspace-umi7).
    /// Given the cursor position, current scroll, viewport height, and article
    /// length, returns the scroll offset that keeps the cursor inside the
    /// rendered viewport.
    ///
    /// <para>
    /// Behaviour:
    /// </para>
    /// <list type="bullet">
    ///   <item>If <paramref name="cursor"/> is already inside the viewport
    ///   (<c>scroll &lt;= cursor &lt; scroll + vpHeight</c>) the scroll offset
    ///   is unchanged.</item>
    ///   <item>If the cursor advanced past the bottom, scroll jumps a full
    ///   page so the cursor lands at the top of the new viewport — the
    ///   "minimize visual jitter during speed reading" choice from
    ///   workspace-1a49ee9.</item>
    ///   <item>If the cursor went above the viewport (back-jumping), scroll
    ///   snaps so the cursor is the top line.</item>
    ///   <item>Scroll is always clamped to <c>[0, max(0, totalLines - vpHeight)]</c>.</item>
    /// </list>
    /// </summary>
    internal static int ScrollForCursor(int cursor, int scroll, int vpHeight, int totalLines)
    {
        var maxScroll = Math.Max(0, totalLines - vpHeight);

        // Cursor below the viewport → jump a full page (or to maxScroll).
        if (cursor >= scroll + vpHeight)
        {
            return Math.Clamp(cursor, 0, maxScroll);
        }

        // Cursor above the viewport → bring it to the top.
        if (cursor < scroll)
        {
            return Math.Clamp(cursor, 0, maxScroll);
        }

        return scroll;
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
