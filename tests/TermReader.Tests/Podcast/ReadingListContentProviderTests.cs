// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Unit")]
public class ReadingListContentProviderTests
{
    private readonly IPageLoader _pageLoader;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPreloadService _preloadService;
    private readonly IPageCache _pageCache;
    private readonly ReadingListContentProvider _provider;

    public ReadingListContentProviderTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _preloadService = Substitute.For<IPreloadService>();
        _pageCache = Substitute.For<IPageCache>();

        var browserConfig = Options.Create(new BrowserConfiguration { Headless = true });

        _provider = new ReadingListContentProvider(
            _pageLoader,
            _contentExtractor,
            browserConfig,
            _preloadService,
            _pageCache,
            NullLogger<ReadingListContentProvider>.Instance);
    }

    private static Collection CreateCollection(params string[] urls)
    {
        var collection = Collection.Create("Test Collection");
        foreach (var url in urls)
        {
            collection.AddItem(url, $"Article: {url}");
        }

        return collection;
    }

    private static ReadableContent CreateReadableContent(string title = "Test Article")
    {
        return ReadableContent.Create(
            title,
            "Some article text content here.",
            new List<string> { "Some article text content here." });
    }

    private static PageLoadResult CreateSuccessResult(string url, string html = "<html><body><p>Content</p></body></html>")
    {
        return PageLoadResult.Successful(url, html, new PageMetadata { Title = "Test" });
    }

    #region WaitForInFlightAsync Integration

    [Fact]
    public async Task LoadAndExtract_WaitsForInFlightPreload_WhenNotCached()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        var preloadResult = CreateSuccessResult(url);
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(preloadResult);
        _contentExtractor.ExtractAsync(preloadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Test Article");
        // Should NOT have called pageLoader since preload completed
        await _pageLoader.DidNotReceive().LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_SkipsWait_WhenAlreadyCached()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _pageCache.Contains(url).Returns(true);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        // Should NOT have checked in-flight since it's already cached
        await _preloadService.DidNotReceive().WaitForInFlightAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_FallsBackToPageLoader_WhenNoInFlightPreload()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_FallsBackToPageLoader_WhenPreloadReturnsNoContent()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        var emptyPreload = new PageLoadResult { Success = true, Url = url, Html = "" };
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(emptyPreload);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_FallsBackToPageLoader_WhenPreloadExtractionFails()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        var preloadResult = CreateSuccessResult(url, "<html><body>JS shell only</body></html>");
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(preloadResult);
        _contentExtractor.ExtractAsync(preloadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);

        // Second extraction call (from page loader) also returns null to test full fallthrough
        _contentExtractor.ExtractAsync(loadResult.Html, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().BeEmpty();
        await _pageLoader.Received().LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_UsesCorrectTimeout_ForInFlightWait()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        await _provider.GetAllArticleContentAsync(collection);

        await _preloadService.Received(1).WaitForInFlightAsync(
            url,
            TimeSpan.FromSeconds(15),
            Arg.Any<CancellationToken>());
    }

    #endregion
}
