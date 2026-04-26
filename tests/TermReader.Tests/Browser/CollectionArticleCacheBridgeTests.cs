// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using TermReader.Infrastructure.Podcast.Cache;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests that collection item preloading bridges to the article content cache,
/// so pre-loaded collection items are served from the persistent article cache
/// on navigation and cache indicators reflect article cache status.
/// </summary>
[Trait("Category", "Unit")]
public class CollectionArticleCacheBridgeTests : IDisposable
{
    private readonly IPageCache _pageCache;
    private readonly IIdleDetector _idleDetector;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IArticleContentCache _articleContentCache;
    private readonly BackgroundPreloadService _service;

    public CollectionArticleCacheBridgeTests()
    {
        _pageCache = Substitute.For<IPageCache>();
        _idleDetector = Substitute.For<IIdleDetector>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _articleContentCache = Substitute.For<IArticleContentCache>();
        var httpClient = new HttpClient();
        var config = new CacheConfiguration();

        _service = new BackgroundPreloadService(
            _pageCache,
            _idleDetector,
            httpClient,
            config,
            NullLogger<BackgroundPreloadService>.Instance,
            _contentExtractor,
            linkExtractor: null,
            _articleContentCache);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    #region GetArticleCachedUrls

    [Fact]
    public void GetArticleCachedUrls_InitiallyEmpty()
    {
        _service.GetArticleCachedUrls().Should().BeEmpty();
    }

    [Fact]
    public void GetArticleCachedUrls_ReturnsOriginalUrls()
    {
        // Simulate article-cached URLs by writing to the internal dictionary
        var field = typeof(BackgroundPreloadService)
            .GetField("_articleCachedUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, string>)field!.GetValue(_service)!;
        dict["https://example.com/article1"] = "https://example.com/article1";
        dict["https://other.com/article2"] = "https://other.com/article2";

        var urls = _service.GetArticleCachedUrls();

        urls.Should().HaveCount(2);
        urls.Should().Contain("https://example.com/article1");
        urls.Should().Contain("https://other.com/article2");
    }

    #endregion

    #region IsUrlCached includes article cache

    [Fact]
    public void BuildCollectionQueue_SkipsUrlsInArticleCache()
    {
        // Mark a URL as article-cached via reflection
        var field = typeof(BackgroundPreloadService)
            .GetField("_articleCachedUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, string>)field!.GetValue(_service)!;
        dict[UrlNormalizer.Normalize("https://cached.com/article")] = "https://cached.com/article";

        var urls = new List<string>
        {
            "https://cached.com/article",
            "https://uncached.com/article",
        };

        var queue = _service.BuildCollectionQueue(0, urls);

        queue.Should().ContainSingle();
        queue[0].Url.Should().Be("https://uncached.com/article");
    }

    [Fact]
    public void GetProgress_CountsArticleCachedUrls()
    {
        // Set up a collection queue with 2 URLs
        var urls = new List<string>
        {
            "https://a.com/1",
            "https://b.com/2",
        };
        _service.BuildCollectionQueue(0, urls);

        // Mark one as article-cached
        var field = typeof(BackgroundPreloadService)
            .GetField("_articleCachedUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, string>)field!.GetValue(_service)!;
        dict[UrlNormalizer.Normalize("https://a.com/1")] = "https://a.com/1";

        var progress = _service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(2);
        progress.CachedCount.Should().Be(1, "one URL is in the article cache");
    }

    [Fact]
    public void GetProgress_CountsBothPageAndArticleCached()
    {
        // Set up 3 URLs
        var urls = new List<string>
        {
            "https://a.com/1",
            "https://b.com/2",
            "https://c.com/3",
        };
        _service.BuildCollectionQueue(0, urls);

        // One in page cache, one in article cache
        _pageCache.Contains("https://a.com/1").Returns(true);
        var field = typeof(BackgroundPreloadService)
            .GetField("_articleCachedUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, string>)field!.GetValue(_service)!;
        dict[UrlNormalizer.Normalize("https://b.com/2")] = "https://b.com/2";

        var progress = _service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(3);
        progress.CachedCount.Should().Be(2, "one in page cache + one in article cache");
    }

    #endregion

    #region Service without article cache dependencies

    [Fact]
    public void WithoutArticleCache_GetArticleCachedUrls_ReturnsEmpty()
    {
        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance);

        service.GetArticleCachedUrls().Should().BeEmpty();
    }

    [Fact]
    public void WithoutArticleCache_IsUrlCached_OnlyChecksPageCache()
    {
        var pageCache = Substitute.For<IPageCache>();
        using var service = new BackgroundPreloadService(
            pageCache,
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance);

        // Even if we somehow populate the article URL dict, without articleContentCache
        // the IsInArticleCache check returns false
        var field = typeof(BackgroundPreloadService)
            .GetField("_articleCachedUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, string>)field!.GetValue(service)!;
        dict[UrlNormalizer.Normalize("https://a.com/1")] = "https://a.com/1";

        var urls = new List<string> { "https://a.com/1" };
        var queue = service.BuildCollectionQueue(0, urls);

        // Without article cache, the URL is NOT considered cached
        queue.Should().ContainSingle();
        queue[0].Url.Should().Be("https://a.com/1");
    }

    #endregion

    #region TryExtractAndCacheArticleAsync (end-to-end via internal state)

    [Fact]
    public void CollectionItemsFlowThroughSamePreloadPath()
    {
        // Verify that collection items use the same PreloadUrlAsync path
        // by checking they're queued identically to regular items
        var urls = new List<string>
        {
            "https://news.com/article-1",
            "https://blog.com/post-2",
            "https://tech.com/review-3",
        };

        var queue = _service.BuildCollectionQueue(1, urls);

        // All 3 URLs should be in the queue (cross-origin allowed for collections)
        queue.Should().HaveCount(3);

        // Sorted by proximity to selected index (1)
        queue[0].Url.Should().Be("https://blog.com/post-2", "selected item first");
    }

    #endregion

    #region CollectionCacheHelper integration

    [Fact]
    public void CollectionCacheHelper_IncludesArticleCachedUrls()
    {
        // Create a mock preload service with article cached URLs
        var preloadService = Substitute.For<IPreloadService>();
        var pageCache = Substitute.For<IPageCache>();

        pageCache.GetCachedUrls().Returns(new HashSet<string> { "https://a.com/1" });
        preloadService.GetArticleCachedUrls().Returns(new HashSet<string> { "https://b.com/2" });

        var collection = TermReader.Domain.Entities.Collections.Collection.Create("Test Collection");
        collection.AddItem("https://a.com/1", "Article A");
        collection.AddItem("https://b.com/2", "Article B");
        collection.AddItem("https://c.com/3", "Article C");

        var progress = TermReader.Infrastructure.Browser.CommandHandlers.CollectionCacheHelper.GetProgress(
            collection, pageCache, preloadService);

        progress.Articles.Should().HaveCount(3);
        progress.Articles[0].State.Should().Be(ArticleCacheState.Cached, "in page cache");
        progress.Articles[1].State.Should().Be(ArticleCacheState.Cached, "in article cache");
        progress.Articles[2].State.Should().Be(ArticleCacheState.Pending, "not in any cache");
    }

    #endregion
}
