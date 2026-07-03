// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class ReadingListContentProviderTests
{
    private readonly IPageLoader _pageLoader;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPreloadService _preloadService;
    private readonly IPageCache _pageCache;
    private readonly IBrowserSession _browserSession;
    private readonly IPageAccessQueue _pageAccessQueue;
    private readonly IArticleContentCache _articleCache;
    private readonly ReadingListContentProvider _provider;

    public ReadingListContentProviderTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _preloadService = Substitute.For<IPreloadService>();
        _pageCache = Substitute.For<IPageCache>();
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsBrowserAvailable.Returns(true);
        _pageAccessQueue = Substitute.For<IPageAccessQueue>();
        _articleCache = Substitute.For<IArticleContentCache>();

        // Default: AcquireAsync returns a no-op lease
        _pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new PageLease(Substitute.For<Microsoft.Playwright.IPage>(), () => { }));


        _provider = new ReadingListContentProvider(
            _pageLoader,
            _contentExtractor,
            _preloadService,
            _pageCache,
            _browserSession,
            _pageAccessQueue,
            _articleCache,
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

    #region Article Content Cache

    [Fact]
    public async Task LoadAndExtract_ReturnsFromContentCache_WhenCacheHit()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        var cachedArticle = new ExtractedArticle
        {
            Title = "Cached Article",
            CleanedText = "Cached text content.",
            Url = url,
            WordCount = 3,
        };

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(cachedArticle);

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Cached Article");
        results[0].CleanedText.Should().Be("Cached text content.");
        // Should NOT have called any page loading or extraction
        await _pageLoader.DidNotReceive().LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
        await _contentExtractor.DidNotReceive().ExtractAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _preloadService.DidNotReceive().WaitForInFlightAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_SkipsNetworkDelay_WhenContentCacheHit()
    {
        var collection = CreateCollection(
            "https://example.com/article1",
            "https://example.com/article2");

        _articleCache.TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Cached",
                CleanedText = "Text.",
                Url = "https://example.com",
                WordCount = 1,
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _provider.GetAllArticleContentAsync(collection);
        sw.Stop();

        // Both articles should be served from content cache with no 3s network delay
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public async Task LoadAndExtract_CachesExtractedArticle_OnSuccessfulExtraction()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(true);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        await _provider.GetAllArticleContentAsync(collection);

        await _articleCache.Received(1).PutAsync(
            url,
            Arg.Is<ExtractedArticle>(a => a.Title == "Test Article"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_FallsThrough_WhenContentCacheMiss()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(true);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Test Article");
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_ReportsContentCacheMethod_WhenCacheHit()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Cached",
                CleanedText = "Text.",
                Url = url,
                WordCount = 1,
            });

        var progressReports = new List<ContentExtractionProgress>();
        var progress = new Progress<ContentExtractionProgress>(p => progressReports.Add(p));

        await _provider.GetAllArticleContentAsync(collection, progress);

        // Allow async progress reports to complete
        await Task.Delay(100);

        progressReports.Should().Contain(p =>
            p.ExtractionMethod != null &&
            p.ExtractionMethod.Contains("content cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAndExtract_FallsThrough_WhenContentCacheThrows()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns<ExtractedArticle?>(_ => throw new InvalidOperationException("Corrupt cache"));
        _pageCache.Contains(url).Returns(true);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1, "cache read failure should not prevent extraction");
        results[0].Title.Should().Be("Test Article");
        await _pageLoader.Received(1).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_StillReturnsArticle_WhenCachePutThrows()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _articleCache.PutAsync(url, Arg.Any<ExtractedArticle>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("Disk full"));
        _pageCache.Contains(url).Returns(true);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1, "cache write failure should not prevent article return");
        results[0].Title.Should().Be("Test Article");
    }

    #endregion

    #region Layer 3 Bot Challenge Retry

    [Fact]
    public async Task LoadAndExtract_BotChallengeFailure_RetriesWithHeadedBrowser()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        // Layer 1: HTTP fails → JS required
        // Layer 2: Browser returns bot challenge failure (polling timed out)
        var callCount = 0;
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Layer 1: HTTP returns JS-required → no content extracted
                    return PageLoadResult.Successful(url, "<html><body><noscript>Please enable JavaScript</noscript></body></html>", new PageMetadata { Title = "Test" });
                }

                if (callCount == 2)
                {
                    // Layer 2: Browser bot challenge failure
                    return PageLoadResult.Failure("Bot challenge could not be resolved");
                }

                // Layer 3: Headed retry succeeds
                return CreateSuccessResult(url);
            });

        // First extraction returns null (JS shell), third succeeds
        var extractCount = 0;
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                extractCount++;
                return extractCount >= 2 ? CreateReadableContent() : null;
            });

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Test Article");
        // 3 calls: HTTP, Browser (bot challenge), headed retry
        await _pageLoader.Received(3).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_BotChallengeFailure_InHeadedMode_StillRetries()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        // The browser is always headed (never-headless law) — the shared provider IS the headed provider.

        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var callCount = 0;
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Layer 1: HTTP fails → JS required
                    return PageLoadResult.Successful(url, "<html><body><noscript>Please enable JavaScript</noscript></body></html>", new PageMetadata { Title = "Test" });
                }

                if (callCount == 2)
                {
                    // Layer 2: Browser bot challenge failure
                    return PageLoadResult.Failure("Bot challenge could not be resolved");
                }

                // Layer 3: Retry succeeds
                return CreateSuccessResult(url);
            });

        var extractCount = 0;
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                extractCount++;
                return extractCount >= 2 ? CreateReadableContent() : null;
            });

        var results = await _provider.GetAllArticleContentAsync(collection);

        // In headed mode, Layer 3 should still fire (previously was dead code)
        results.Should().HaveCount(1);
        await _pageLoader.Received(3).LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAndExtract_BotChallengeFailure_RestoresWindowAndNotifiesUser()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var callCount = 0;
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return PageLoadResult.Successful(url, "<html><body><noscript>Please enable JavaScript</noscript></body></html>", new PageMetadata { Title = "Test" });
                }

                if (callCount == 2)
                {
                    return PageLoadResult.Failure("Bot challenge could not be resolved");
                }

                return CreateSuccessResult(url);
            });

        var extractCount = 0;
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                extractCount++;
                return extractCount >= 2 ? CreateReadableContent() : null;
            });

        // Track progress reports to verify user notification
        var progressReports = new List<ContentExtractionProgress>();
        var progress = new Progress<ContentExtractionProgress>(p => progressReports.Add(p));

        await _provider.GetAllArticleContentAsync(collection, progress);

        // Verify RestoreWindow was called
        await _browserSession.Received(1).RestoreWindowAsync();

        // Verify progress reported the bot challenge message
        progressReports.Should().Contain(p =>
            p.ExtractionMethod != null &&
            p.ExtractionMethod.Contains("bot challenge", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Cancellation and Timeout

    [Fact]
    public async Task GetAllArticleContentAsync_GlobalCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var collection = CreateCollection("https://example.com/a");
        Func<Task> act = () => _provider.GetAllArticleContentAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetAllArticleContentAsync_PerArticleTimeout_ContinuesToNextArticle()
    {
        var url1 = "https://example.com/slow";
        var url2 = "https://example.com/fast";
        var collection = CreateCollection(url1, url2);

        // First article: simulate timeout by throwing OperationCanceledException
        // from a non-global token (per-article timeout)
        _articleCache.TryGetAsync(url1, Arg.Any<CancellationToken>())
            .Returns<ExtractedArticle?>(callInfo =>
            {
                var token = callInfo.ArgAt<CancellationToken>(1);
                throw new OperationCanceledException(token);
            });

        // Second article: succeeds from cache
        _articleCache.TryGetAsync(url2, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Fast Article",
                CleanedText = "Content.",
                Url = url2,
                WordCount = 1,
            });

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Fast Article");
    }

    [Fact]
    public async Task GetAllArticleContentAsync_PerArticleTimeout_RecordsFailure()
    {
        var url = "https://example.com/slow";
        var collection = CreateCollection(url);

        // Simulate per-article timeout
        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns<ExtractedArticle?>(callInfo =>
            {
                var token = callInfo.ArgAt<CancellationToken>(1);
                throw new OperationCanceledException(token);
            });

        await _provider.GetAllArticleContentAsync(collection);

        _provider.LastExtractionFailures.Should().ContainSingle();
        _provider.LastExtractionFailures[0].Url.Should().Be(url);
        _provider.LastExtractionFailures[0].Reason.Should().Contain("timed out");
    }

    [Fact]
    public async Task GetAllArticleContentAsync_GlobalCancellation_DoesNotCatchAsTimeout()
    {
        var url = "https://example.com/a";
        var collection = CreateCollection(url);

        using var cts = new CancellationTokenSource();

        // Simulate that the global token is cancelled during cache lookup
        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
                return (ExtractedArticle?)null;
            });

        Func<Task> act = () => _provider.GetAllArticleContentAsync(collection, cancellationToken: cts.Token);

        // Global cancellation should propagate, not be caught as per-article timeout
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Layer Fallback Error Propagation

    [Fact]
    public async Task LoadAndExtract_Layer1Throws_FallsThroughToLayer2()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var callCount = 0;
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpRequestException("DNS resolution failed");
                }

                // Layer 2 succeeds
                return CreateSuccessResult(url);
            });

        _contentExtractor.ExtractAsync(Arg.Any<string>(), url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        callCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task LoadAndExtract_DriverCrash_SkipsBrowserForRemainingArticles()
    {
        var url1 = "https://example.com/article1";
        var url2 = "https://example.com/article2";
        var collection = CreateCollection(url1, url2);

        _articleCache.TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(Arg.Any<string>()).Returns(false);
        _preloadService.WaitForInFlightAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var callCount = 0;
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Layer 1 for article 1: no content
                    return PageLoadResult.Successful(url1, "<html></html>", new PageMetadata { Title = "Test" });
                }

                if (callCount == 2)
                {
                    // Layer 2 for article 1: driver crash
                    return PageLoadResult.Failure("session is no longer available");
                }

                // All subsequent calls (article 2): no content
                return PageLoadResult.Successful(url2, "<html></html>", new PageMetadata { Title = "Test" });
            });

        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        await _provider.GetAllArticleContentAsync(collection);

        // Article 1: Layer 1 + Layer 2 (crash) = 2 calls
        // Article 2: Layer 1 only (selenium skipped due to crash) = 1 call
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task LoadAndExtract_GenericException_RecordsFailureAndContinues()
    {
        var url1 = "https://example.com/failing";
        var url2 = "https://example.com/good";
        var collection = CreateCollection(url1, url2);

        // First article: all layers throw
        _articleCache.TryGetAsync(url1, Arg.Any<CancellationToken>())
            .Returns<ExtractedArticle?>(_ => throw new InvalidOperationException("Corrupt data"));
        _pageCache.Contains(url1).Returns(false);
        _preloadService.WaitForInFlightAsync(url1, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<PageLoadResult?>(_ => throw new InvalidOperationException("Preload error"));
        _pageLoader.LoadAsync(
                Arg.Is<PageLoadRequest>(r => r.Url == url1),
                Arg.Any<CancellationToken>())
            .Returns<PageLoadResult>(_ => throw new InvalidOperationException("Load error"));

        // Second article: succeeds from cache
        _articleCache.TryGetAsync(url2, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Good Article",
                CleanedText = "Content.",
                Url = url2,
                WordCount = 1,
            });

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Good Article");
        _provider.LastExtractionFailures.Should().ContainSingle(f => f.Url == url1);
    }

    [Fact]
    public async Task LoadAndExtract_AllLayersFail_ReturnsEmptyWithFailures()
    {
        var url = "https://example.com/failing";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        // All pageLoader calls return failure or no extractable content
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Failure("Connection refused"));

        var results = await _provider.GetAllArticleContentAsync(collection);

        results.Should().BeEmpty();
        _provider.LastExtractionFailures.Should().ContainSingle();
        _provider.LastExtractionFailures[0].Reason.Should().Contain("No readable content");
    }

    #endregion

    #region PageAccessQueue Integration

    [Fact]
    public async Task Layer1_SetsPreferBrowserTrue_OnPageLoadRequest()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(true);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        await _provider.GetAllArticleContentAsync(collection);

        await _pageLoader.Received(1).LoadAsync(
            Arg.Is<PageLoadRequest>(r => r.PreferBrowser == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Layer1_AcquiresBackgroundLease_BeforeLoading()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(true);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        await _provider.GetAllArticleContentAsync(collection);

        await _pageAccessQueue.Received().AcquireAsync(
            PageAccessPriority.Background,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Layer2_AcquiresBackgroundLease_ForBrowserRetry()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var callCount = 0;
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Layer 1: HTTP returns empty content
                    return PageLoadResult.Successful(url, "<html></html>", new PageMetadata { Title = "Test" });
                }

                // Layer 2: Browser succeeds
                return CreateSuccessResult(url);
            });

        // Layer 1 extraction returns null, Layer 2 succeeds
        var extractCount = 0;
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                extractCount++;
                return extractCount >= 2 ? CreateReadableContent() : null;
            });

        await _provider.GetAllArticleContentAsync(collection);

        // Should have acquired Background lease twice (Layer 1 + Layer 2)
        await _pageAccessQueue.Received(2).AcquireAsync(
            PageAccessPriority.Background,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Layer3_AcquiresBackgroundLease_ForBotChallengeRetry()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var callCount = 0;
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount <= 1)
                {
                    // Layer 1: JS shell
                    return PageLoadResult.Successful(url, "<html><body><noscript>JS required</noscript></body></html>", new PageMetadata { Title = "Test" });
                }

                if (callCount == 2)
                {
                    // Layer 2: Bot challenge
                    return PageLoadResult.Failure("Bot challenge could not be resolved");
                }

                // Layer 3: Success
                return CreateSuccessResult(url);
            });

        var extractCount = 0;
        _contentExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                extractCount++;
                return extractCount >= 2 ? CreateReadableContent() : null;
            });

        await _provider.GetAllArticleContentAsync(collection);

        // Layer 1 + Layer 2 + Layer 3 = 3 Background lease acquisitions
        await _pageAccessQueue.Received(3).AcquireAsync(
            PageAccessPriority.Background,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PageAccessQueue_LeaseIsDisposed_AfterLoadCompletes()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(true);

        var leaseDisposed = false;
        var lease = new PageLease(
            Substitute.For<Microsoft.Playwright.IPage>(),
            () => leaseDisposed = true);
        _pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(lease);

        var loadResult = CreateSuccessResult(url);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(loadResult);
        _contentExtractor.ExtractAsync(loadResult.Html, url, Arg.Any<CancellationToken>())
            .Returns(CreateReadableContent());

        await _provider.GetAllArticleContentAsync(collection);

        leaseDisposed.Should().BeTrue("lease should be disposed after the load completes");
    }

    [Fact]
    public async Task PageAccessQueue_NeverUsesForegroundPriority()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url).Returns(false);
        _preloadService.WaitForInFlightAsync(url, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        // All layers fail so all get exercised
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Failure("Connection refused"));

        await _provider.GetAllArticleContentAsync(collection);

        await _pageAccessQueue.DidNotReceive().AcquireAsync(
            PageAccessPriority.Foreground,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PageAccessQueue_NotAcquired_WhenArticleServedFromContentCache()
    {
        var url = "https://example.com/article1";
        var collection = CreateCollection(url);

        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Title = "Cached",
                CleanedText = "Text.",
                Url = url,
                WordCount = 1,
            });

        await _provider.GetAllArticleContentAsync(collection);

        await _pageAccessQueue.DidNotReceive().AcquireAsync(
            Arg.Any<PageAccessPriority>(),
            Arg.Any<CancellationToken>());
    }

    #endregion
}
