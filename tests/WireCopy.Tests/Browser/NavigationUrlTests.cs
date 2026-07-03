// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-khpe.3: the shared normalize-and-validate step for user-typed
/// navigation targets (':open' argument and the launcher 'o' URL bar).
/// </summary>
[Trait("Category", "Unit")]
public class NavigationUrlTests
{
    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("http://example.com/path", "http://example.com/path")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("news.ycombinator.com/newest", "https://news.ycombinator.com/newest")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("localhost:8080", "https://localhost:8080")]
    public void TryNormalize_AcceptsAndNormalizesValidInput(string input, string expected)
    {
        var ok = NavigationUrl.TryNormalize(input, out var url, out var error);

        ok.Should().BeTrue();
        url.Should().Be(expected);
        error.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_RejectsBlankInput(string? input)
    {
        var ok = NavigationUrl.TryNormalize(input, out var url, out var error);

        ok.Should().BeFalse();
        url.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("has space.com")]        // whitespace inside the host is not a valid URL
    [InlineData("http://")]              // scheme with no host
    [InlineData("https://")]             // scheme with no host
    [InlineData("ht!tp://weird")]        // gets https:// prepended → unparseable authority
    public void TryNormalize_RejectsMalformedInput(string input)
    {
        var ok = NavigationUrl.TryNormalize(input, out var url, out var error);

        ok.Should().BeFalse();
        url.Should().BeEmpty();
        error.Should().Contain("Not a valid URL");
    }
}
