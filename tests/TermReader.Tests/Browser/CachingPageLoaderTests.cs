// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

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

        result.Should().Be(cached);
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
    public async Task GetPageSourceAsync_DelegatesToInner()
    {
        _innerLoader.GetPageSourceAsync("https://example.com", Arg.Any<CancellationToken>())
            .Returns("<html>source</html>");

        var result = await _sut.GetPageSourceAsync("https://example.com");

        result.Should().Be("<html>source</html>");
    }

    private static PageLoadResult CreateResult(string url, string html = "<html>test</html>")
    {
        return PageLoadResult.Successful(url, html, new PageMetadata { Title = "Test" });
    }
}
