// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the speed reading helper methods exposed as internal static
/// on BrowserOrchestrator via InternalsVisibleTo.
/// </summary>
[Trait("Category", "Unit")]
public class SpeedReadingTests
{
    // CountWordsStrippingAnsi tests

    [Fact]
    public void CountWords_PlainText_CountsCorrectly()
    {
        BrowserOrchestrator.CountWordsStrippingAnsi("hello world foo bar").Should().Be(4);
    }

    [Fact]
    public void CountWords_EmptyString_ReturnsZero()
    {
        BrowserOrchestrator.CountWordsStrippingAnsi("").Should().Be(0);
    }

    [Fact]
    public void CountWords_WhitespaceOnly_ReturnsZero()
    {
        BrowserOrchestrator.CountWordsStrippingAnsi("   ").Should().Be(0);
    }

    [Fact]
    public void CountWords_WithAnsiCodes_StripsBeforeCounting()
    {
        // "\x1b[1m" = bold, "\x1b[0m" = reset
        var text = "\x1b[1mBold text\x1b[0m and normal";
        BrowserOrchestrator.CountWordsStrippingAnsi(text).Should().Be(4);
    }

    [Fact]
    public void CountWords_OnlyAnsiCodes_ReturnsZero()
    {
        BrowserOrchestrator.CountWordsStrippingAnsi("\x1b[31m\x1b[0m").Should().Be(0);
    }

    [Fact]
    public void CountWords_MultipleSpaces_CountsWordsNotSpaces()
    {
        BrowserOrchestrator.CountWordsStrippingAnsi("  hello   world  ").Should().Be(2);
    }

    // ComputeLineDelayMs tests

    [Fact]
    public void ComputeDelay_NormalLine_UsesWordCountAndWpm()
    {
        // 5 words at 300 WPM = 5 * 200 = 1000ms - 100ms overhead = 900ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs("one two three four five", 300, nextLineBlank: false);
        delay.Should().Be(900);
    }

    [Fact]
    public void ComputeDelay_ParagraphBoundary_Adds150ms()
    {
        // 5 words at 300 WPM = 1000ms - 100ms overhead + 150ms paragraph = 1050ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs("one two three four five", 300, nextLineBlank: true);
        delay.Should().Be(1050);
    }

    [Fact]
    public void ComputeDelay_EmptyLine_Returns30msFloor()
    {
        BrowserOrchestrator.ComputeLineDelayMs("", 250, nextLineBlank: false).Should().Be(30);
    }

    [Fact]
    public void ComputeDelay_WhitespaceOnlyLine_Returns30msFloor()
    {
        BrowserOrchestrator.ComputeLineDelayMs("   ", 250, nextLineBlank: false).Should().Be(30);
    }

    [Fact]
    public void ComputeDelay_MinimumFloor_30ms()
    {
        // 1 word at 1000 WPM = 60ms - 100ms overhead = floor at 30ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs("word", 1000, nextLineBlank: false);
        delay.Should().Be(30);

        // Even at extreme WPM with empty words, floor should hold
        var floorDelay = BrowserOrchestrator.ComputeLineDelayMs("  ", 1000, nextLineBlank: false);
        floorDelay.Should().Be(30);
    }

    [Fact]
    public void ComputeDelay_DefaultWpm250_CompensatesForRenderOverhead()
    {
        // 10 words at 250 WPM = 10 * 240 = 2400ms - 100ms overhead = 2300ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs(
            "The quick brown fox jumps over the lazy sleeping dog",
            250,
            nextLineBlank: false);
        delay.Should().Be(2300);
    }

    [Fact]
    public void ComputeDelay_SingleWordLine_ShortDelay()
    {
        // 1 word at 250 WPM = 240ms - 100ms overhead = 140ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs("Hello", 250, nextLineBlank: false);
        delay.Should().Be(140);
    }

    [Fact]
    public void ComputeDelay_AnsiLine_StripsAnsiBeforeComputing()
    {
        // 2 actual words + ANSI codes at 300 WPM = 2 * 200 = 400ms - 100ms overhead = 300ms
        var line = "\x1b[1mBold\x1b[0m text";
        var delay = BrowserOrchestrator.ComputeLineDelayMs(line, 300, nextLineBlank: false);
        delay.Should().Be(300);
    }

    // Measured-overhead tests — the new behavior subtracts the actual render
    // time of the previous tick instead of a fixed 100ms guess.

    [Fact]
    public void ComputeDelay_MeasuredOverhead_SubtractsActualRenderTime()
    {
        // 5 words at 300 WPM = 1000ms total. Measured render took 250ms,
        // so the next timer should be 1000 - 250 = 750ms (NOT 900ms).
        var delay = BrowserOrchestrator.ComputeLineDelayMs(
            "one two three four five",
            wpm: 300,
            nextLineBlank: false,
            renderOverheadMs: 250);
        delay.Should().Be(750);
    }

    [Fact]
    public void ComputeDelay_MeasuredOverheadZero_NoSubtraction()
    {
        // If the render is effectively instantaneous, the timer equals the
        // full WPM-implied duration. 5 words at 300 WPM = 1000ms.
        var delay = BrowserOrchestrator.ComputeLineDelayMs(
            "one two three four five",
            wpm: 300,
            nextLineBlank: false,
            renderOverheadMs: 0);
        delay.Should().Be(1000);
    }

    [Fact]
    public void ComputeDelay_MeasuredOverheadDefault_Matches100msFallback()
    {
        // Omitting renderOverheadMs uses the 100ms default, so behavior matches
        // the original three-arg call site (back-compat).
        var legacyDelay = BrowserOrchestrator.ComputeLineDelayMs(
            "one two three four five",
            300,
            nextLineBlank: false);
        var explicitDefault = BrowserOrchestrator.ComputeLineDelayMs(
            "one two three four five",
            300,
            nextLineBlank: false,
            renderOverheadMs: BrowserOrchestrator.DefaultRenderOverheadMs);
        legacyDelay.Should().Be(explicitDefault);
        legacyDelay.Should().Be(900);
    }

    [Fact]
    public void ComputeDelay_MeasuredOverheadLargerThanBudget_FloorsAt30ms()
    {
        // 1 word at 250 WPM = 240ms, but measured render took 500ms (slow terminal).
        // 240 - 500 = -260ms, must floor at 30ms.
        var delay = BrowserOrchestrator.ComputeLineDelayMs(
            "Hello",
            wpm: 250,
            nextLineBlank: false,
            renderOverheadMs: 500);
        delay.Should().Be(30);
    }

    [Fact]
    public void ComputeDelay_MeasuredOverhead_PreservesParagraphBonus()
    {
        // 5 words at 300 WPM = 1000ms + 150ms paragraph bonus = 1150ms.
        // Measured render = 200ms → 950ms.
        var delay = BrowserOrchestrator.ComputeLineDelayMs(
            "one two three four five",
            wpm: 300,
            nextLineBlank: true,
            renderOverheadMs: 200);
        delay.Should().Be(950);
    }

    [Fact]
    public void ComputeDelay_NegativeOverhead_TreatedAsZero()
    {
        // Defensive: a negative measurement (clock skew, weird stopwatch behavior)
        // must not inflate the delay. 5 words at 300 WPM = 1000ms unchanged.
        var delay = BrowserOrchestrator.ComputeLineDelayMs(
            "one two three four five",
            wpm: 300,
            nextLineBlank: false,
            renderOverheadMs: -50);
        delay.Should().Be(1000);
    }
}
