// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.ScrapingStrategies;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-felb: hierarchy config keys and url patterns must carry a
/// non-default port so localhost sites on different ports never share a
/// config, while real-world domains keep their bare-host keys.
/// </summary>
[Trait("Category", "Unit")]
public class HierarchyDomainKeyTests
{
    [Theory]
    [InlineData("https://www.nytimes.com/section/todayspaper", "www.nytimes.com")]
    [InlineData("https://example.com:443/", "example.com")]
    [InlineData("http://example.com:80/", "example.com")]
    [InlineData("http://127.0.0.1:8642/", "127.0.0.1:8642")]
    [InlineData("http://localhost:5173/index.html", "localhost:5173")]
    [InlineData("HTTP://LOCALHOST:5173/", "localhost:5173")]
    public void FromUrl_KeysNonDefaultPortsAndBareHostsCorrectly(string url, string expected)
    {
        HierarchyDomainKey.FromUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("not-a-valid-url")]
    [InlineData("")]
    public void TryFromUrl_InvalidUrl_ReturnsNull(string url)
    {
        HierarchyDomainKey.TryFromUrl(url).Should().BeNull();
        HierarchyDomainKey.FromUrl(url).Should().Be("unknown");
    }

    [Fact]
    public void BuildUrlPattern_NonDefaultPort_IncludesPortLiteral()
    {
        var pattern = DocumentOrderStrategy.BuildUrlPattern("http://127.0.0.1:8642/");

        pattern.Should().Be("^https?://(www\\.)?127\\.0\\.0\\.1:8642/?");
        System.Text.RegularExpressions.Regex.IsMatch("http://127.0.0.1:8642/", pattern).Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch("http://127.0.0.1:9000/", pattern).Should().BeFalse();
    }

    [Fact]
    public void BuildUrlPattern_DefaultPort_OmitsPort()
    {
        var pattern = DocumentOrderStrategy.BuildUrlPattern("https://www.nytimes.com/section/todayspaper");

        pattern.Should().Be("^https?://(www\\.)?www\\.nytimes\\.com/section/todayspaper");
        System.Text.RegularExpressions.Regex.IsMatch("https://www.nytimes.com/section/todayspaper", pattern).Should().BeTrue();
    }
}
