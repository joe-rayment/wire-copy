// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class PodcastCacheWaitTests
{
    #region CollectionCacheProgress DTO

    [Fact]
    public void CollectionCacheProgress_AllCached_IsComplete()
    {
        var progress = new CollectionCacheProgress
        {
            Articles =
            [
                new ArticleCacheStatus { Url = "https://a.com", Title = "A", State = ArticleCacheState.Cached },
                new ArticleCacheStatus { Url = "https://b.com", Title = "B", State = ArticleCacheState.Cached },
            ],
        };

        progress.IsComplete.Should().BeTrue();
        progress.CachedCount.Should().Be(2);
        progress.Total.Should().Be(2);
        progress.PendingCount.Should().Be(0);
        progress.NeedsBrowserCount.Should().Be(0);
    }

    [Fact]
    public void CollectionCacheProgress_AllNeedsBrowser_IsComplete()
    {
        var progress = new CollectionCacheProgress
        {
            Articles =
            [
                new ArticleCacheStatus { Url = "https://a.com", Title = "A", State = ArticleCacheState.NeedsBrowser },
                new ArticleCacheStatus { Url = "https://b.com", Title = "B", State = ArticleCacheState.NeedsBrowser },
            ],
        };

        progress.IsComplete.Should().BeTrue();
        progress.NeedsBrowserCount.Should().Be(2);
        progress.CachedCount.Should().Be(0);
    }

    [Fact]
    public void CollectionCacheProgress_MixedCachedAndNeedsBrowser_IsComplete()
    {
        var progress = new CollectionCacheProgress
        {
            Articles =
            [
                new ArticleCacheStatus { Url = "https://a.com", Title = "A", State = ArticleCacheState.Cached },
                new ArticleCacheStatus { Url = "https://b.com", Title = "B", State = ArticleCacheState.NeedsBrowser },
            ],
        };

        progress.IsComplete.Should().BeTrue();
        progress.CachedCount.Should().Be(1);
        progress.NeedsBrowserCount.Should().Be(1);
    }

    [Fact]
    public void CollectionCacheProgress_HasPending_IsNotComplete()
    {
        var progress = new CollectionCacheProgress
        {
            Articles =
            [
                new ArticleCacheStatus { Url = "https://a.com", Title = "A", State = ArticleCacheState.Cached },
                new ArticleCacheStatus { Url = "https://b.com", Title = "B", State = ArticleCacheState.Pending },
            ],
        };

        progress.IsComplete.Should().BeFalse();
        progress.PendingCount.Should().Be(1);
    }

    [Fact]
    public void CollectionCacheProgress_HasCaching_IsNotComplete()
    {
        var progress = new CollectionCacheProgress
        {
            Articles =
            [
                new ArticleCacheStatus { Url = "https://a.com", Title = "A", State = ArticleCacheState.Cached },
                new ArticleCacheStatus { Url = "https://b.com", Title = "B", State = ArticleCacheState.Caching },
            ],
        };

        progress.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void CollectionCacheProgress_Empty_IsComplete()
    {
        var progress = new CollectionCacheProgress
        {
            Articles = [],
        };

        progress.IsComplete.Should().BeTrue();
        progress.Total.Should().Be(0);
    }

    #endregion

    #region PreloadProgress.IsComplete

    [Fact]
    public void PreloadProgress_AllCached_IsComplete()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 5,
            NeedsBrowserCount = 0,
        };

        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PreloadProgress_AllNeedsBrowser_IsComplete()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 3,
            CachedCount = 0,
            NeedsBrowserCount = 3,
        };

        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PreloadProgress_MixedCachedAndNeedsBrowser_IsComplete()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 4,
            CachedCount = 2,
            NeedsBrowserCount = 2,
        };

        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PreloadProgress_Partial_IsNotComplete()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 2,
            NeedsBrowserCount = 1,
        };

        progress.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void PreloadProgress_ZeroTotal_IsComplete()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 0,
            CachedCount = 0,
            NeedsBrowserCount = 0,
        };

        progress.IsComplete.Should().BeTrue();
    }

    #endregion

    #region CollectionCacheHelper.GetProgress

    [Fact]
    public void GetProgress_AllUrlsCached_ReturnsAllCached()
    {
        var collection = CreateCollection("https://a.com/1", "https://b.com/2", "https://c.com/3");

        var pageCache = Substitute.For<IPageCache>();
        pageCache.GetCachedUrls().Returns(new HashSet<string>
        {
            "https://a.com/1",
            "https://b.com/2",
            "https://c.com/3",
        });

        var preloadService = Substitute.For<IPreloadService>();

        var progress = CollectionCacheHelper.GetProgress(collection, pageCache, preloadService);

        progress.Articles.Should().HaveCount(3);
        progress.Articles.Should().AllSatisfy(a => a.State.Should().Be(ArticleCacheState.Cached));
        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void GetProgress_NoUrlsCached_ReturnsAllPending()
    {
        var collection = CreateCollection("https://a.com/1", "https://b.com/2");

        var pageCache = Substitute.For<IPageCache>();
        pageCache.GetCachedUrls().Returns(new HashSet<string>());

        var preloadService = Substitute.For<IPreloadService>();

        var progress = CollectionCacheHelper.GetProgress(collection, pageCache, preloadService);

        progress.Articles.Should().HaveCount(2);
        progress.Articles.Should().AllSatisfy(a => a.State.Should().Be(ArticleCacheState.Pending));
        progress.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void GetProgress_SomeUrlsCached_ReturnsMixedStates()
    {
        var collection = CreateCollection("https://a.com/1", "https://b.com/2", "https://c.com/3");

        var pageCache = Substitute.For<IPageCache>();
        pageCache.GetCachedUrls().Returns(new HashSet<string> { "https://a.com/1", "https://c.com/3" });

        var preloadService = Substitute.For<IPreloadService>();

        var progress = CollectionCacheHelper.GetProgress(collection, pageCache, preloadService);

        progress.Articles[0].State.Should().Be(ArticleCacheState.Cached);
        progress.Articles[1].State.Should().Be(ArticleCacheState.Pending);
        progress.Articles[2].State.Should().Be(ArticleCacheState.Cached);
        progress.CachedCount.Should().Be(2);
        progress.PendingCount.Should().Be(1);
    }

    [Fact]
    public void GetProgress_PreservesArticleOrder()
    {
        var collection = CreateCollection("https://z.com", "https://a.com", "https://m.com");

        var pageCache = Substitute.For<IPageCache>();
        pageCache.GetCachedUrls().Returns(new HashSet<string>());

        var preloadService = Substitute.For<IPreloadService>();

        var progress = CollectionCacheHelper.GetProgress(collection, pageCache, preloadService);

        progress.Articles[0].Url.Should().Be("https://z.com");
        progress.Articles[1].Url.Should().Be("https://a.com");
        progress.Articles[2].Url.Should().Be("https://m.com");
    }

    [Fact]
    public void GetProgress_IncludesTitlesFromCollection()
    {
        var collection = Collection.Create("Test");
        collection.AddItem("https://a.com", "First Article");
        collection.AddItem("https://b.com", "Second Article");

        var pageCache = Substitute.For<IPageCache>();
        pageCache.GetCachedUrls().Returns(new HashSet<string>());

        var preloadService = Substitute.For<IPreloadService>();

        var progress = CollectionCacheHelper.GetProgress(collection, pageCache, preloadService);

        progress.Articles[0].Title.Should().Be("First Article");
        progress.Articles[1].Title.Should().Be("Second Article");
    }

    [Fact]
    public void GetProgress_EmptyCollection_ReturnsEmptyProgress()
    {
        var collection = Collection.Create("Empty");

        var pageCache = Substitute.For<IPageCache>();
        pageCache.GetCachedUrls().Returns(new HashSet<string>());

        var preloadService = Substitute.For<IPreloadService>();

        var progress = CollectionCacheHelper.GetProgress(collection, pageCache, preloadService);

        progress.Articles.Should().BeEmpty();
        progress.Total.Should().Be(0);
        progress.IsComplete.Should().BeTrue();
    }

    #endregion

    #region Cache-wait skip conditions

    [Fact]
    public void CacheWaitSkipped_WhenPreloadProgressIsComplete()
    {
        // The cache-wait screen checks PreloadProgress.IsComplete first.
        // When all cacheable links are processed, the screen should be skipped.
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 3,
            CachedCount = 2,
            NeedsBrowserCount = 1,
        };

        progress.IsComplete.Should().BeTrue("cache-wait screen should be skipped when all links are cached or need browser");
    }

    [Fact]
    public void CacheWaitContinues_WhenPreloadProgressIsNotComplete()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 2,
            NeedsBrowserCount = 0,
        };

        progress.IsComplete.Should().BeFalse("cache-wait screen should continue polling when links remain uncached");
    }

    [Fact]
    public void CacheWaitAutoProceeds_WhenAllCached()
    {
        // Simulates all URLs transitioning to cached state.
        // PreloadProgress.IsComplete becomes true, breaking the wait loop.
        var initialProgress = new PreloadProgress
        {
            TotalCacheableLinks = 3,
            CachedCount = 1,
            NeedsBrowserCount = 0,
        };
        initialProgress.IsComplete.Should().BeFalse();

        var finalProgress = new PreloadProgress
        {
            TotalCacheableLinks = 3,
            CachedCount = 3,
            NeedsBrowserCount = 0,
        };
        finalProgress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void CacheWaitAutoProceeds_WhenRemainingUrlsNeedBrowser()
    {
        // When all remaining URLs are identified as needing browser rendering,
        // the preload service marks itself complete and the wait loop breaks.
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 4,
            CachedCount = 1,
            NeedsBrowserCount = 3,
        };

        progress.IsComplete.Should().BeTrue("remaining URLs all need browser, so preloading is done");
    }

    #endregion

    #region Helpers

    private static Collection CreateCollection(params string[] urls)
    {
        var collection = Collection.Create("Test Collection");
        foreach (var url in urls)
        {
            collection.AddItem(url, $"Article: {url}");
        }

        return collection;
    }

    #endregion
}
