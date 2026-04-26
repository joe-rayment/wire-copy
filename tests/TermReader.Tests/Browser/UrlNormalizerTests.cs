// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Infrastructure.Browser.Cache;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class UrlNormalizerTests
{
    [Theory]
    [InlineData("https://Example.COM/path", "https://example.com/path")]
    [InlineData("HTTPS://EXAMPLE.COM/Path", "https://example.com/Path")]
    public void Normalize_LowercasesSchemeAndHost(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/path/", "https://example.com/path")]
    [InlineData("https://example.com/path//", "https://example.com/path")]
    public void Normalize_StripsTrailingSlash(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_PreservesRootSlash()
    {
        UrlNormalizer.Normalize("https://example.com/").Should().Be("https://example.com/");
    }

    [Theory]
    [InlineData("https://example.com/page#section", "https://example.com/page")]
    [InlineData("https://example.com/page#", "https://example.com/page")]
    public void Normalize_StripsFragments(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/page?utm_source=twitter", "https://example.com/page")]
    [InlineData("https://example.com/page?utm_medium=social&utm_campaign=test", "https://example.com/page")]
    [InlineData("https://example.com/page?fbclid=abc123", "https://example.com/page")]
    [InlineData("https://example.com/page?gclid=xyz", "https://example.com/page")]
    public void Normalize_StripsTrackingParams(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/page?id=42&utm_source=twitter", "https://example.com/page?id=42")]
    [InlineData("https://example.com/page?utm_source=twitter&id=42", "https://example.com/page?id=42")]
    public void Normalize_KeepsNonTrackingParams(string input, string expected)
    {
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_HandlesEmptyString()
    {
        UrlNormalizer.Normalize("").Should().BeEmpty();
        UrlNormalizer.Normalize("  ").Should().BeEmpty();
    }

    [Fact]
    public void Normalize_HandlesNonAbsoluteUrl()
    {
        // Non-absolute paths may be resolved by UriBuilder; just verify it doesn't throw
        var result = UrlNormalizer.Normalize("/relative/path");
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Normalize_CombinedNormalization()
    {
        var input = "HTTPS://Example.COM/page?utm_source=twitter&id=42#section";
        var expected = "https://example.com/page?id=42";
        UrlNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void GetOrigin_ReturnsSchemeAndHost()
    {
        UrlNormalizer.GetOrigin("https://example.com/page/1").Should().Be("https://example.com");
    }

    [Fact]
    public void GetOrigin_IncludesNonDefaultPort()
    {
        UrlNormalizer.GetOrigin("https://example.com:8443/page").Should().Be("https://example.com:8443");
    }

    [Fact]
    public void GetOrigin_ReturnsNullForInvalidUrl()
    {
        UrlNormalizer.GetOrigin("not-a-url").Should().BeNull();
    }

    [Fact]
    public void IsSameOrigin_TrueForSameOrigin()
    {
        UrlNormalizer.IsSameOrigin(
            "https://example.com/page1",
            "https://example.com/page2").Should().BeTrue();
    }

    [Fact]
    public void IsSameOrigin_FalseForDifferentHosts()
    {
        UrlNormalizer.IsSameOrigin(
            "https://example.com/page",
            "https://other.com/page").Should().BeFalse();
    }

    [Fact]
    public void IsSameOrigin_FalseForDifferentSchemes()
    {
        UrlNormalizer.IsSameOrigin(
            "http://example.com/page",
            "https://example.com/page").Should().BeFalse();
    }

    [Fact]
    public void IsSameOrigin_CaseInsensitive()
    {
        UrlNormalizer.IsSameOrigin(
            "https://Example.COM/page1",
            "https://example.com/page2").Should().BeTrue();
    }
}
