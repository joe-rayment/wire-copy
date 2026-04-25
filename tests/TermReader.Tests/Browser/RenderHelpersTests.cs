// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class RenderHelpersTests
{
    #region WrapText

    [Fact]
    public void WrapText_EmptyString_ReturnsEmptyList()
    {
        var result = RenderHelpers.WrapText("", 10);
        result.Should().BeEmpty();
    }

    [Fact]
    public void WrapText_MaxWidthZero_ReturnsEmptyList()
    {
        var result = RenderHelpers.WrapText("hello world", 0);
        result.Should().BeEmpty();
    }

    [Fact]
    public void WrapText_MaxWidthNegative_ReturnsEmptyList()
    {
        var result = RenderHelpers.WrapText("hello world", -5);
        result.Should().BeEmpty();
    }

    [Fact]
    public void WrapText_SingleWordFitsExactly_ReturnsSingleLine()
    {
        var result = RenderHelpers.WrapText("hello", 5);
        result.Should().Equal("hello");
    }

    [Fact]
    public void WrapText_TextFitsInOneLine_ReturnsSingleLine()
    {
        var result = RenderHelpers.WrapText("hello world", 20);
        result.Should().Equal("hello world");
    }

    [Fact]
    public void WrapText_NormalWrapping_BreaksAtWordBoundary()
    {
        var result = RenderHelpers.WrapText("hello world test", 8);
        result.Should().Equal("hello", "world", "test");
    }

    [Fact]
    public void WrapText_WordLongerThanMaxWidth_HardBreaksWord()
    {
        var result = RenderHelpers.WrapText("abcdefghij", 4);
        result.Should().Equal("abcd", "efgh", "ij");
    }

    [Fact]
    public void WrapText_MixedLongAndShortWords_WrapsCorrectly()
    {
        var result = RenderHelpers.WrapText("hi abcdefghij ok", 5);
        result.Should().Equal("hi", "abcde", "fghij", "ok");
    }

    [Fact]
    public void WrapText_MultipleSpaces_TreatsAsOneDelimiter()
    {
        var result = RenderHelpers.WrapText("hello  world", 20);
        result.Should().Equal("hello world");
    }

    [Fact]
    public void WrapText_WhitespaceOnly_ReturnsEmptyList()
    {
        var result = RenderHelpers.WrapText("   ", 10);
        result.Should().BeEmpty();
    }

    #endregion

    #region TruncateText

    [Fact]
    public void TruncateText_NullInput_ReturnsEmpty()
    {
        var result = RenderHelpers.TruncateText(null!, 10);
        result.Should().BeEmpty();
    }

    [Fact]
    public void TruncateText_EmptyInput_ReturnsEmpty()
    {
        var result = RenderHelpers.TruncateText("", 10);
        result.Should().BeEmpty();
    }

    [Fact]
    public void TruncateText_TextShorterThanMax_ReturnsUnchanged()
    {
        var result = RenderHelpers.TruncateText("hello", 10);
        result.Should().Be("hello");
    }

    [Fact]
    public void TruncateText_TextExactlyMax_ReturnsUnchanged()
    {
        var result = RenderHelpers.TruncateText("hello", 5);
        result.Should().Be("hello");
    }

    [Fact]
    public void TruncateText_TextLongerThanMax_TruncatesWithEllipsis()
    {
        var result = RenderHelpers.TruncateText("hello world", 8);
        result.Should().Be("hello w\u2026");
    }

    [Fact]
    public void TruncateText_MaxLengthThree_TruncatesWithEllipsis()
    {
        var result = RenderHelpers.TruncateText("hello", 3);
        result.Should().Be("he\u2026");
    }

    [Fact]
    public void TruncateText_MaxLengthOne_NoEllipsis()
    {
        var result = RenderHelpers.TruncateText("hello", 1);
        result.Should().Be("h");
    }

    [Fact]
    public void TruncateText_MaxLengthFour_TruncatesWithEllipsis()
    {
        var result = RenderHelpers.TruncateText("hello", 4);
        result.Should().Be("hel\u2026");
    }

    #endregion

    #region TruncateUrl

    [Fact]
    public void TruncateUrl_NullInput_ReturnsNull()
    {
        var result = RenderHelpers.TruncateUrl(null!, 10);
        result.Should().BeNull();
    }

    [Fact]
    public void TruncateUrl_EmptyInput_ReturnsEmpty()
    {
        var result = RenderHelpers.TruncateUrl("", 10);
        result.Should().BeEmpty();
    }

    [Fact]
    public void TruncateUrl_UrlShorterThanMax_ReturnsUnchanged()
    {
        var result = RenderHelpers.TruncateUrl("https://example.com", 30);
        result.Should().Be("https://example.com");
    }

    [Fact]
    public void TruncateUrl_UrlExactlyMax_ReturnsUnchanged()
    {
        var url = "https://example.com";
        var result = RenderHelpers.TruncateUrl(url, url.Length);
        result.Should().Be(url);
    }

    [Fact]
    public void TruncateUrl_UrlLongerThanMax_TruncatesMiddle()
    {
        var result = RenderHelpers.TruncateUrl("https://example.com/very/long/path", 20);
        // halfLen = (20-1)/2 = 9
        result.Should().Be("https://e\u2026long/path");
    }

    [Fact]
    public void TruncateUrl_VeryShortMax_StillProducesResult()
    {
        var result = RenderHelpers.TruncateUrl("https://example.com", 5);
        // halfLen = (5-1)/2 = 2
        result.Should().Be("ht\u2026om");
    }

    #endregion

    #region GetDisplayWidth

    [Fact]
    public void GetDisplayWidth_AsciiText_ReturnsLength()
    {
        RenderHelpers.GetDisplayWidth("hello").Should().Be(5);
    }

    [Fact]
    public void GetDisplayWidth_EmptyString_ReturnsZero()
    {
        RenderHelpers.GetDisplayWidth("").Should().Be(0);
        RenderHelpers.GetDisplayWidth(null!).Should().Be(0);
    }

    [Fact]
    public void GetDisplayWidth_CjkCharacters_ReturnDoubleWidth()
    {
        // Each CJK character = 2 columns
        RenderHelpers.GetDisplayWidth("漢字").Should().Be(4);
        RenderHelpers.GetDisplayWidth("こんにちは").Should().Be(10);
    }

    [Fact]
    public void GetDisplayWidth_MixedAsciiAndCjk_ReturnsCorrectWidth()
    {
        // "Hello" (5) + "世界" (4) = 9
        RenderHelpers.GetDisplayWidth("Hello世界").Should().Be(9);
    }

    [Fact]
    public void GetDisplayWidth_AnsiEscapeSequences_SkippedInWidth()
    {
        // ANSI codes have zero display width
        RenderHelpers.GetDisplayWidth("\x1b[31mred\x1b[0m").Should().Be(3);
        RenderHelpers.GetDisplayWidth("\x1b[38;5;220mhello\x1b[0m").Should().Be(5);
    }

    [Fact]
    public void GetDisplayWidth_FullwidthForms_ReturnDoubleWidth()
    {
        // Fullwidth Latin 'Ａ' (U+FF21) = 2 columns
        RenderHelpers.GetDisplayWidth("\uFF21").Should().Be(2);
    }

    #endregion

    #region GetCharDisplayWidth

    [Fact]
    public void GetCharDisplayWidth_Ascii_ReturnsOne()
    {
        RenderHelpers.GetCharDisplayWidth('A').Should().Be(1);
        RenderHelpers.GetCharDisplayWidth(' ').Should().Be(1);
    }

    [Fact]
    public void GetCharDisplayWidth_CjkUnified_ReturnsTwo()
    {
        RenderHelpers.GetCharDisplayWidth('漢').Should().Be(2);
    }

    [Fact]
    public void GetCharDisplayWidth_Hangul_ReturnsTwo()
    {
        RenderHelpers.GetCharDisplayWidth('한').Should().Be(2);
    }

    #endregion

    #region TruncateText with Unicode

    [Fact]
    public void TruncateText_CjkText_TruncatesByDisplayWidth()
    {
        // "漢字漢字漢字" = 12 display cols. Truncate to 8 = "漢字漢字" (8-1=7 display cols) + "…" (1) = 8
        var result = RenderHelpers.TruncateText("漢字漢字漢字", 8);
        result.Should().EndWith("\u2026");
        RenderHelpers.GetDisplayWidth(result).Should().BeLessOrEqualTo(8);
    }

    [Fact]
    public void TruncateText_CjkTextFits_ReturnsOriginal()
    {
        RenderHelpers.TruncateText("漢字", 10).Should().Be("漢字");
    }

    #endregion

    #region WrapText with Unicode

    [Fact]
    public void WrapText_CjkWord_WrapsAtDisplayWidth()
    {
        // "漢字漢字" = 8 cols, maxWidth = 6 -> should wrap
        var result = RenderHelpers.WrapText("漢字漢字", 6);
        result.Should().HaveCountGreaterThan(1);
        foreach (var line in result)
        {
            RenderHelpers.GetDisplayWidth(line).Should().BeLessOrEqualTo(6);
        }
    }

    #endregion

    #region FormatCacheAge

    [Fact]
    public void FormatCacheAge_Null_ReturnsJustNow()
    {
        RenderHelpers.FormatCacheAge(null).Should().Be("just now");
    }

    [Fact]
    public void FormatCacheAge_LessThanOneMinute_ReturnsLessThan1m()
    {
        RenderHelpers.FormatCacheAge(DateTime.UtcNow.AddSeconds(-30)).Should().Be("<1m ago");
    }

    [Fact]
    public void FormatCacheAge_FiveMinutesAgo_Returns5m()
    {
        RenderHelpers.FormatCacheAge(DateTime.UtcNow.AddMinutes(-5)).Should().Be("5m ago");
    }

    [Fact]
    public void FormatCacheAge_TwoHoursAgo_Returns2h()
    {
        RenderHelpers.FormatCacheAge(DateTime.UtcNow.AddHours(-2)).Should().Be("2h ago");
    }

    #endregion
}
