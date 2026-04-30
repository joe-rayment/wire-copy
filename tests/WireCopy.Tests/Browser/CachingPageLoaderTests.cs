// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class CachingPageLoaderTests : IDisposable
{
    private readonly IPageLoader _innerLoader;
    private readonly InMemoryPageCache _cache;
    private readonly CachingPageLoader _sut;

    public CachingPageLoaderTests()
    {
        _innerLoader = Substitute.For<IPageLoader>();
        _cache = new InMemoryPageCache(
            Options.Create(new CacheConfiguration { EvictionSweepIntervalSeconds = 3600 }),
            NullLogger<InMemoryPageCache>.Instance);
        _sut = new CachingPageLoader(
            _innerLoader,
            _cache,
            NullLogger<CachingPageLoader>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    [Fact]
    public async Task LoadAsync_CacheMiss_DelegatesToInnerLoader()
    {
        var request = new PageLoadRequest { Url = "https://example.com/page" };
        var expected = CreateResult("https://example.com/page");
        _innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.LoadAsync(request);

        result.Should().Be(expected);
        await _innerLoader.Received(1).LoadAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_CacheMiss_StoresResultInCache()
    {
        var request = new PageLoadRequest { Url = "https://example.com/page" };
        var expected = CreateResult("https://example.com/page");
        _innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(expected);

        await _sut.LoadAsync(request);

        _cache.Contains("https://example.com/page").Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_CacheHit_ReturnsCachedResult()
    {
        var url = "https://example.com/page";
        var cached = CreateResult(url);
        _cache.Put(url, cached);

        var request = new PageLoadRequest { Url = url };
        var result = await _sut.LoadAsync(request);

        result.Url.Should().Be(cached.Url);
        result.Html.Should().Be(cached.Html);
        result.Success.Should().Be(cached.Success);
        result.FetchMethod.Should().Be(FetchMethod.Cached);
        await _innerLoader.DidNotReceive().LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_ForceRefresh_BypassesCache()
    {
        var url = "https://example.com/page";
        var cached = CreateResult(url, "<html>old</html>");
        _cache.Put(url, cached);

        var fresh = CreateResult(url, "<html>new</html>");
        var request = new PageLoadRequest { Url = url, ForceRefresh = true };
        _innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(fresh);

        var result = await _sut.LoadAsync(request);

        result.Html.Should().Be("<html>new</html>");
        await _innerLoader.Received(1).LoadAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_ForceRefresh_UpdatesCache()
    {
        var url = "https://example.com/page";
        _cache.Put(url, CreateResult(url, "<html>old</html>"));

        var fresh = CreateResult(url, "<html>new</html>");
        var request = new PageLoadRequest { Url = url, ForceRefresh = true };
        _innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(fresh);

        await _sut.LoadAsync(request);

        var fromCache = _cache.TryGet(url);
        fromCache.Should().NotBeNull();
        fromCache!.Html.Should().Be("<html>new</html>");
    }

    [Fact]
    public async Task LoadAsync_FailedResult_DoesNotCache()
    {
        var request = new PageLoadRequest { Url = "https://example.com/fail" };
        var failure = PageLoadResult.Failure("error");
        _innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(failure);

        await _sut.LoadAsync(request);

        _cache.Contains("https://example.com/fail").Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_CacheHit_LowQuality_FallsThrough()
    {
        var url = "https://example.com/page";
        var thinHtml = "<html><body><p>Enable JavaScript</p></body></html>";
        _cache.Put(url, CreateResult(url, thinHtml));

        var freshResult = CreateResult(url);
        _innerLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(freshResult);

        var request = new PageLoadRequest { Url = url };
        var result = await _sut.LoadAsync(request);

        result.Html.Should().Be(freshResult.Html);
        await _innerLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
        _cache.Contains(url).Should().BeTrue("fresh result should be cached");
    }

    [Theory]
    [InlineData("<html><body><p>short</p></body></html>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasSufficientContent_BelowThreshold_ReturnsFalse(string? html, bool expected)
    {
        CachingPageLoader.HasSufficientContent(html, 50).Should().Be(expected);
    }

    [Fact]
    public void HasSufficientContent_RichContent_ReturnsTrue()
    {
        CachingPageLoader.HasSufficientContent(RichHtml, 50).Should().BeTrue();
    }

    [Fact]
    public void HasSufficientContent_ScriptHeavyPage_ReturnsFalse()
    {
        var html = "<html><body>" +
            "<script>var x = 'lots of words inside script tags that should not count " +
            "towards the word total because they are not visible to the user at all';</script>" +
            "<style>.class { background: url('more words here in stylesheet rules'); }</style>" +
            "<p>Only three words</p>" +
            "</body></html>";
        CachingPageLoader.HasSufficientContent(html, 50).Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_ForceBrowser_BypassesCache()
    {
        var url = "https://nytimes.com/article";
        var cached = CreateResult(url, "<html>cached section page</html>");
        _cache.Put(url, cached);

        var fresh = CreateResult(url, "<html>real article via browser</html>");
        var request = new PageLoadRequest { Url = url, ForceBrowser = true };
        _innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(fresh);

        var result = await _sut.LoadAsync(request);

        result.Html.Should().Be("<html>real article via browser</html>");
        await _innerLoader.Received(1).LoadAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_ForceBrowser_StillCachesResult()
    {
        var url = "https://nytimes.com/article";
        var fresh = CreateResult(url);
        var request = new PageLoadRequest { Url = url, ForceBrowser = true };
        _innerLoader.LoadAsync(request, Arg.Any<CancellationToken>()).Returns(fresh);

        await _sut.LoadAsync(request);

        _cache.Contains(url).Should().BeTrue("ForceBrowser bypasses cache read but still stores result");
    }

    [Fact]
    public async Task GetPageSourceAsync_DelegatesToInner()
    {
        _innerLoader.GetPageSourceAsync("https://example.com", Arg.Any<CancellationToken>())
            .Returns("<html>source</html>");

        var result = await _sut.GetPageSourceAsync("https://example.com");

        result.Should().Be("<html>source</html>");
    }

    private const string RichHtml =
        "<html><body><article>" +
        "<p>This is a long article with enough words to pass the quality threshold. " +
        "It contains multiple sentences with varied vocabulary and structure. " +
        "The purpose of this text is to simulate a real web page that has been " +
        "properly loaded with actual content rather than a paywall or JavaScript " +
        "shell page. We need at least fifty words to pass the minimum threshold " +
        "that prevents serving degraded cached content to users.</p>" +
        "</article></body></html>";

    private static PageLoadResult CreateResult(string url, string? html = null)
    {
        return PageLoadResult.Successful(url, html ?? RichHtml, new PageMetadata { Title = "Test" });
    }
}
