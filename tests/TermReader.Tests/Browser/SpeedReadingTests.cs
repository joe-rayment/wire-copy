// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

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
        // 5 words at 300 WPM = 5 * (60000/300) = 5 * 200 = 1000ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs("one two three four five", 300, nextLineBlank: false);
        delay.Should().Be(1000);
    }

    [Fact]
    public void ComputeDelay_ParagraphBoundary_Adds300ms()
    {
        // 5 words at 300 WPM = 1000ms + 300ms paragraph pause = 1300ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs("one two three four five", 300, nextLineBlank: true);
        delay.Should().Be(1300);
    }

    [Fact]
    public void ComputeDelay_EmptyLine_Returns50msFloor()
    {
        BrowserOrchestrator.ComputeLineDelayMs("", 250, nextLineBlank: false).Should().Be(50);
    }

    [Fact]
    public void ComputeDelay_WhitespaceOnlyLine_Returns50msFloor()
    {
        BrowserOrchestrator.ComputeLineDelayMs("   ", 250, nextLineBlank: false).Should().Be(50);
    }

    [Fact]
    public void ComputeDelay_MinimumFloor_50ms()
    {
        // 1 word at 1000 WPM = 60ms, above floor
        var delay = BrowserOrchestrator.ComputeLineDelayMs("word", 1000, nextLineBlank: false);
        delay.Should().Be(60);

        // Even at extreme WPM with empty words, floor should hold
        var floorDelay = BrowserOrchestrator.ComputeLineDelayMs("  ", 1000, nextLineBlank: false);
        floorDelay.Should().Be(50);
    }

    [Fact]
    public void ComputeDelay_DefaultWpm250_CalculatesCorrectly()
    {
        // 10 words at 250 WPM = 10 * 240 = 2400ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs(
            "The quick brown fox jumps over the lazy sleeping dog",
            250,
            nextLineBlank: false);
        delay.Should().Be(2400);
    }

    [Fact]
    public void ComputeDelay_SingleWordLine_ShortDelay()
    {
        // 1 word at 250 WPM = 240ms
        var delay = BrowserOrchestrator.ComputeLineDelayMs("Hello", 250, nextLineBlank: false);
        delay.Should().Be(240);
    }

    [Fact]
    public void ComputeDelay_AnsiLine_StripsAnsiBeforeComputing()
    {
        // 2 actual words + ANSI codes at 300 WPM = 2 * 200 = 400ms
        var line = "\x1b[1mBold\x1b[0m text";
        var delay = BrowserOrchestrator.ComputeLineDelayMs(line, 300, nextLineBlank: false);
        delay.Should().Be(400);
    }
}
