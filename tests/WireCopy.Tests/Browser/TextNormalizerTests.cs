// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

public class TextNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("Foo&nbsp;Bar", "Foo Bar")]
    [InlineData("AT&amp;T", "AT&T")]
    [InlineData("&#39;quoted&#39;", "'quoted'")]
    [InlineData("&#x27;hex-quoted&#x27;", "'hex-quoted'")]
    [InlineData("em &mdash; dash", "em — dash")]
    [InlineData("a &lt; b &gt; c", "a < b > c")]
    [InlineData("&amp;amp;", "&amp;")]
    public void NormalizeDisplayText_DecodesAndCleans(string? input, string expected)
    {
        TextNormalizer.NormalizeDisplayText(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeDisplayText_ConvertsNonBreakingSpaceToRegularSpace()
    {
        // U+00A0 input directly (e.g. some feeds emit the codepoint, not the entity)
        var input = "Foo Bar";
        TextNormalizer.NormalizeDisplayText(input).Should().Be("Foo Bar");
    }

    [Fact]
    public void NormalizeDisplayText_CollapsesMultipleWhitespaceRuns()
    {
        TextNormalizer.NormalizeDisplayText("  Foo  \t Bar\n\n  Baz  ")
            .Should().Be("Foo Bar Baz");
    }

    [Fact]
    public void NormalizeDisplayText_TrimsLeadingAndTrailingWhitespace()
    {
        TextNormalizer.NormalizeDisplayText("   leading and trailing   ")
            .Should().Be("leading and trailing");
    }

    [Fact]
    public void NormalizeDisplayText_HandlesMixedEntitiesAndWhitespace()
    {
        TextNormalizer.NormalizeDisplayText(
                "Foo&nbsp;Bar &amp; Baz &#39;quote&#39; &mdash; em-dash")
            .Should().Be("Foo Bar & Baz 'quote' — em-dash");
    }

    [Fact]
    public void NormalizeDisplayText_NbspBetweenWordsCollapsesNoExtraSpace()
    {
        // &nbsp; → U+00A0 → ' ', so two adjacent spaces collapse to one.
        TextNormalizer.NormalizeDisplayText("A &nbsp; B")
            .Should().Be("A B");
    }
}
