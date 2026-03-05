// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

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
        result.Should().Be("hello...");
    }

    [Fact]
    public void TruncateText_MaxLengthThree_NoEllipsis()
    {
        var result = RenderHelpers.TruncateText("hello", 3);
        result.Should().Be("hel");
    }

    [Fact]
    public void TruncateText_MaxLengthOne_ReturnsSingleChar()
    {
        var result = RenderHelpers.TruncateText("hello", 1);
        result.Should().Be("h");
    }

    [Fact]
    public void TruncateText_MaxLengthFour_TruncatesWithEllipsis()
    {
        var result = RenderHelpers.TruncateText("hello", 4);
        result.Should().Be("h...");
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
        // halfLen = (20-3)/2 = 8
        result.Should().Be("https://...ong/path");
    }

    [Fact]
    public void TruncateUrl_VeryShortMax_StillProducesResult()
    {
        var result = RenderHelpers.TruncateUrl("https://example.com", 5);
        // halfLen = (5-3)/2 = 1
        result.Should().Be("h...m");
    }

    #endregion
}
