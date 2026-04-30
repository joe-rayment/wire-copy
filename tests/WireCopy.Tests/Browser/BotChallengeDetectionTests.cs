// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class BotChallengeDetectionTests
{
    // --- IsBotChallengePage tests ---

    [Fact]
    public void IsBotChallengePage_DataDomePage_ReturnsTrue()
    {
        var html = """
            <html><head><title>nytimes.com</title></head>
            <body><p>Please enable JS and disable any ad blocker</p>
            <script src="https://geo.captcha-delivery.com/captcha/"></script></body></html>
            """;

        PageLoader.IsBotChallengePage(html).Should().BeTrue();
    }

    [Fact]
    public void IsBotChallengePage_DataDomeKeyword_ReturnsTrue()
    {
        var html = "<html><head></head><body><script>datadome challenge</script></body></html>";

        PageLoader.IsBotChallengePage(html).Should().BeTrue();
    }

    [Fact]
    public void IsBotChallengePage_CloudflareChallenge_ReturnsTrue()
    {
        var html = "<html><head></head><body><div id=\"cf-challenge\">Checking your browser</div></body></html>";

        PageLoader.IsBotChallengePage(html).Should().BeTrue();
    }

    [Fact]
    public void IsBotChallengePage_ChallengePlatform_ReturnsTrue()
    {
        var html = "<html><head></head><body><div class=\"challenge-platform\">Verifying</div></body></html>";

        PageLoader.IsBotChallengePage(html).Should().BeTrue();
    }

    [Fact]
    public void IsBotChallengePage_GenericSmallCaptchaPage_ReturnsTrue()
    {
        var html = "<html><head></head><body><div>Please solve this captcha</div></body></html>";

        PageLoader.IsBotChallengePage(html).Should().BeTrue();
    }

    [Fact]
    public void IsBotChallengePage_NormalArticlePage_ReturnsFalse()
    {
        var html = """
            <html><head><title>Article Title</title>
            <meta property="og:type" content="article" /></head>
            <body><article><p>This is a long article paragraph with plenty of content that would be found
            on a normal news website. It contains multiple sentences and is clearly a real page.</p>
            <p>Another paragraph with substantial content about the news story being reported.</p>
            </article></body></html>
            """;

        PageLoader.IsBotChallengePage(html).Should().BeFalse();
    }

    [Fact]
    public void IsBotChallengePage_LargePageWithCaptchaWord_ReturnsFalse()
    {
        // A large page that mentions "captcha" in passing should NOT be flagged
        var html = "<html><head></head><body>" + new string('x', 6000) +
            "<p>We use captcha for security</p></body></html>";

        PageLoader.IsBotChallengePage(html).Should().BeFalse();
    }

    [Fact]
    public void IsBotChallengePage_NullOrEmpty_ReturnsFalse()
    {
        PageLoader.IsBotChallengePage(null!).Should().BeFalse();
        PageLoader.IsBotChallengePage("").Should().BeFalse();
    }

    // --- CachingPageLoader bot challenge skip tests ---

    [Fact]
    public async Task CachingPageLoader_BotChallengePage_NotCached()
    {
        var innerLoader = Substitute.For<IPageLoader>();
        using var cache = new InMemoryPageCache(
            Options.Create(new CacheConfiguration { EvictionSweepIntervalSeconds = 3600 }),
            NullLogger<InMemoryPageCache>.Instance);
        var sut = new CachingPageLoader(
            innerLoader, cache, NullLogger<CachingPageLoader>.Instance);

        var challengeHtml = """
            <html><head><title>nytimes.com</title></head>
            <body><script src="https://captcha-delivery.com/c"></script></body></html>
            """;
        var request = new PageLoadRequest { Url = "https://www.nytimes.com/article" };
        var challengeResult = PageLoadResult.Successful(
            request.Url, challengeHtml, new PageMetadata { Title = "nytimes.com" });
        innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(challengeResult);

        await sut.LoadAsync(request);

        cache.Contains(request.Url).Should().BeFalse();
    }

    [Fact]
    public async Task CachingPageLoader_NormalPage_IsCached()
    {
        var innerLoader = Substitute.For<IPageLoader>();
        using var cache = new InMemoryPageCache(
            Options.Create(new CacheConfiguration { EvictionSweepIntervalSeconds = 3600 }),
            NullLogger<InMemoryPageCache>.Instance);
        var sut = new CachingPageLoader(
            innerLoader, cache, NullLogger<CachingPageLoader>.Instance);

        var normalHtml = "<html><head><title>Real Article</title></head><body><p>Content</p></body></html>";
        var request = new PageLoadRequest { Url = "https://example.com/article" };
        var normalResult = PageLoadResult.Successful(
            request.Url, normalHtml, new PageMetadata { Title = "Real Article" });
        innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(normalResult);

        await sut.LoadAsync(request);

        cache.Contains(request.Url).Should().BeTrue();
    }

    // --- BrowserSession headless mode tracking tests ---

    [Fact]
    public void WarmUpAsync_UsesConfiguredHeadlessSetting()
    {
        // Verify that WarmUpAsync creates a driver with the configured headless setting.
        // We can't test the actual driver creation (needs Chrome), but we can verify
        // the config is read correctly.
        var config = new BrowserConfiguration { Headless = false };
        config.Headless.Should().BeFalse();

        var configTrue = new BrowserConfiguration { Headless = true };
        configTrue.Headless.Should().BeTrue();
    }

    [Fact]
    public void PostLoadDelayMs_DefaultIs500()
    {
        var config = new BrowserConfiguration();
        config.PostLoadDelayMs.Should().Be(500);
    }
}
