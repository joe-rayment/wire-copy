// Educational and personal use only.

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using TermReader.Infrastructure.Podcast.Cache;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class BackgroundPreloadServiceTests : IDisposable
{
    private readonly IPageCache _cache;
    private readonly IIdleDetector _idleDetector;
    private readonly CacheConfiguration _config;
    private readonly BackgroundPreloadService _service;

    public BackgroundPreloadServiceTests()
    {
        _cache = Substitute.For<IPageCache>();
        _idleDetector = Substitute.For<IIdleDetector>();
        var httpClient = new HttpClient();
        _config = new CacheConfiguration();
        _service = new BackgroundPreloadService(
            _cache, _idleDetector, httpClient, _config,
            NullLogger<BackgroundPreloadService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    #region BuildQueue - Proximity-Based Ordering

    [Fact]
    public void BuildQueue_SortedByProximityToSelectedIndex()
    {
        var urls = Enumerable.Range(1, 5).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        // Selected index is 2 (middle); items should be sorted by distance from cursor
        var queue = _service.BuildQueue(2, nodes, "https://example.com");

        queue.Should().HaveCount(5);
        queue[0].ListIndex.Should().Be(2, "distance 0 from cursor");
        queue[1].ListIndex.Should().Be(1, "distance 1, tiebreaker: lower index first");
        queue[2].ListIndex.Should().Be(3, "distance 1, tiebreaker: higher index second");
        queue[3].ListIndex.Should().Be(0, "distance 2, tiebreaker: lower index first");
        queue[4].ListIndex.Should().Be(4, "distance 2, tiebreaker: higher index second");
    }

    [Fact]
    public void BuildQueue_SelectedAtStart_SortsByDistanceFromStart()
    {
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2", "https://example.com/a3");

        var queue = _service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().HaveCount(3);
        queue[0].ListIndex.Should().Be(0, "selected item first");
        queue[1].ListIndex.Should().Be(1, "distance 1");
        queue[2].ListIndex.Should().Be(2, "distance 2");
    }

    [Fact]
    public void BuildQueue_SelectedAtEnd_SortsByDistanceFromEnd()
    {
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2", "https://example.com/a3");

        var queue = _service.BuildQueue(2, nodes, "https://example.com");

        queue.Should().HaveCount(3);
        queue[0].ListIndex.Should().Be(2, "selected item first");
        queue[1].ListIndex.Should().Be(1, "distance 1");
        queue[2].ListIndex.Should().Be(0, "distance 2");
    }

    [Fact]
    public void BuildQueue_SelectedIndexAffectsOrdering()
    {
        var urls = Enumerable.Range(1, 5).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        // Different selected indices should produce different orderings
        var queueStart = _service.BuildQueue(0, nodes, "https://example.com");
        var queueEnd = _service.BuildQueue(4, nodes, "https://example.com");

        queueStart.Select(i => i.Url).Should().NotEqual(queueEnd.Select(i => i.Url));
    }

    [Fact]
    public void BuildQueue_MixedNodeTypes_SortedByProximity()
    {
        var root = LinkNode.CreateRoot();
        var groupHeader = LinkInfo.CreateGroupHeader(LinkType.Content);
        var article1 = new LinkInfo { Url = "https://example.com/a1", DisplayText = "A1", Type = LinkType.Content, ImportanceScore = 50 };
        var article2 = new LinkInfo { Url = "https://example.com/a2", DisplayText = "A2", Type = LinkType.Content, ImportanceScore = 50 };
        root.AddChild(groupHeader);  // index 0 (skipped)
        root.AddChild(article1);     // index 1
        root.AddChild(groupHeader);  // index 2 (skipped)
        root.AddChild(article2);     // index 3
        var nodes = root.Children.ToList();

        // Selected at index 3: article2 (distance 0) should come before article1 (distance 2)
        var queue = _service.BuildQueue(3, nodes, "https://example.com");

        queue.Should().HaveCount(2);
        queue[0].ListIndex.Should().Be(3, "article2 is at selected index");
        queue[1].ListIndex.Should().Be(1, "article1 is farther from cursor");
    }

    #endregion

    #region ComputePriorityScore

    [Theory]
    [InlineData(5, 5, 0)]   // At cursor: distance 0
    [InlineData(3, 5, 2)]   // Before cursor: distance 2
    [InlineData(7, 5, 2)]   // After cursor: distance 2
    [InlineData(0, 10, 10)] // Far before cursor
    [InlineData(10, 0, 10)] // Far after cursor
    public void ComputePriorityScore_ReturnsDistanceFromCursor(int listIndex, int selectedIndex, int expectedScore)
    {
        var score = BackgroundPreloadService.ComputePriorityScore(listIndex, selectedIndex);

        score.Should().Be(expectedScore);
    }

    [Fact]
    public void ComputePriorityScore_SymmetricAroundCursor()
    {
        // Items equidistant on either side of cursor should have equal scores
        var scoreBefore = BackgroundPreloadService.ComputePriorityScore(3, 5);
        var scoreAfter = BackgroundPreloadService.ComputePriorityScore(7, 5);

        scoreBefore.Should().Be(scoreAfter);
    }

    [Fact]
    public void ComputePriorityScore_CloserItemsHaveLowerScore()
    {
        var scoreClose = BackgroundPreloadService.ComputePriorityScore(4, 5);
        var scoreFar = BackgroundPreloadService.ComputePriorityScore(0, 5);

        scoreClose.Should().BeLessThan(scoreFar);
    }

    #endregion

    #region BuildQueue - Filtering

    [Fact]
    public void BuildQueue_FiltersOutExternalOriginLinks()
    {
        var nodes = CreateContentNodes("https://example.com/a1", "https://other.com/a2", "https://example.com/a3");

        var queue = _service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().NotContain(i => i.Url == "https://other.com/a2");
    }

    [Fact]
    public void BuildQueue_FiltersOutNonContentLinks()
    {
        var root = LinkNode.CreateRoot();
        var contentLink = new LinkInfo
        {
            Url = "https://example.com/article",
            DisplayText = "Article",
            Type = LinkType.Content,
            ImportanceScore = 50
        };
        var navLink = new LinkInfo
        {
            Url = "https://example.com/nav",
            DisplayText = "Nav",
            Type = LinkType.Navigation,
            ImportanceScore = 50
        };
        root.AddChild(contentLink);
        root.AddChild(navLink);
        var nodes = root.Children.ToList();

        var queue = _service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().NotContain(i => i.Url == "https://example.com/nav");
        queue.Should().Contain(i => i.Url == "https://example.com/article");
    }

    [Fact]
    public void BuildQueue_SkipsCachedUrls()
    {
        _cache.Contains("https://example.com/a1").Returns(true);
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");

        var queue = _service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().NotContain(i => i.Url == "https://example.com/a1");
        queue.Should().Contain(i => i.Url == "https://example.com/a2");
    }

    [Fact]
    public void BuildQueue_SkipsGroupHeaders()
    {
        var root = LinkNode.CreateRoot();
        var groupHeader = LinkInfo.CreateGroupHeader(LinkType.Content);
        var contentLink = new LinkInfo
        {
            Url = "https://example.com/article",
            DisplayText = "Article",
            Type = LinkType.Content,
            ImportanceScore = 50
        };
        root.AddChild(groupHeader);
        root.AddChild(contentLink);
        var nodes = root.Children.ToList();

        var queue = _service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().HaveCount(1);
        queue[0].Url.Should().Be("https://example.com/article");
    }

    [Fact]
    public void BuildQueue_EmptyNodes_ReturnsEmptyQueue()
    {
        var nodes = new List<LinkNode>();

        var queue = _service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().BeEmpty();
    }

    [Fact]
    public void BuildQueue_FiltersOutPaywalledDomains()
    {
        var browserConfig = new BrowserConfiguration { PaywalledDomains = ["nytimes.com"] };
        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            browserConfig: browserConfig);

        var nodes = CreateContentNodes(
            "https://www.nytimes.com/2026/01/article1",
            "https://www.nytimes.com/2026/01/article2",
            "https://www.nytimes.com/2026/01/article3");

        var queue = service.BuildQueue(0, nodes, "https://www.nytimes.com");

        queue.Should().BeEmpty("paywalled domains cannot be HTTP-cached");
    }

    [Fact]
    public void BuildQueue_PaywalledDomains_ProgressShowsZeroEligible()
    {
        var browserConfig = new BrowserConfiguration { PaywalledDomains = ["nytimes.com"] };
        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            browserConfig: browserConfig);

        var nodes = CreateContentNodes(
            "https://www.nytimes.com/2026/01/article1",
            "https://www.nytimes.com/2026/01/article2");

        service.BuildQueue(0, nodes, "https://www.nytimes.com");
        var progress = service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(0, "paywalled URLs should not count as cacheable");
        progress.IsActivelyFetching.Should().BeFalse();
    }

    [Fact]
    public void BuildCollectionQueue_FiltersOutPaywalledDomains()
    {
        var browserConfig = new BrowserConfiguration { PaywalledDomains = ["nytimes.com"] };
        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            browserConfig: browserConfig);

        var urls = new List<string>
        {
            "https://www.nytimes.com/2026/01/article1",
            "https://example.com/article2",
            "https://www.nytimes.com/2026/01/article3",
        };

        var queue = service.BuildCollectionQueue(0, urls);

        queue.Should().HaveCount(1);
        queue[0].Url.Should().Be("https://example.com/article2");
    }

    [Fact]
    public void BuildCollectionQueue_PaywalledDomains_ProgressExcludesPaywalled()
    {
        var browserConfig = new BrowserConfiguration { PaywalledDomains = ["nytimes.com"] };
        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            browserConfig: browserConfig);

        var urls = new List<string>
        {
            "https://www.nytimes.com/2026/01/article1",
            "https://example.com/article2",
        };

        service.BuildCollectionQueue(0, urls);
        var progress = service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(1, "only non-paywalled URL should be counted");
    }

    #endregion

    #region IsBotDetectionResponse

    [Theory]
    [InlineData("Attention Required! | Cloudflare")]
    [InlineData("<html>You have been blocked</html>")]
    [InlineData("<div>Checking your browser before accessing</div>")]
    [InlineData("<div id='cf-challenge'>challenge</div>")]
    [InlineData("<html>Please enable JavaScript to continue</html>")]
    [InlineData("<html>Access Denied</html>")]
    public void IsBotDetectionResponse_DetectsBotPages(string html)
    {
        BackgroundPreloadService.IsBotDetectionResponse(html).Should().BeTrue();
    }

    [Theory]
    [InlineData("<html><body>Normal page content</body></html>")]
    [InlineData("<html><head><title>News Article</title></head></html>")]
    [InlineData("")]
    public void IsBotDetectionResponse_AcceptsNormalPages(string html)
    {
        BackgroundPreloadService.IsBotDetectionResponse(html).Should().BeFalse();
    }

    #endregion

    #region IsRedirectedUrl

    [Fact]
    public void IsRedirectedUrl_SameUrl_ReturnsFalse()
    {
        BackgroundPreloadService.IsRedirectedUrl(
            "https://nytimes.com/2024/01/15/article",
            "https://nytimes.com/2024/01/15/article").Should().BeFalse();
    }

    [Fact]
    public void IsRedirectedUrl_DifferentPath_ReturnsTrue()
    {
        // Article URL redirected to section page
        BackgroundPreloadService.IsRedirectedUrl(
            "https://nytimes.com/2024/01/15/us/article-slug.html",
            "https://nytimes.com/section/us").Should().BeTrue();
    }

    [Fact]
    public void IsRedirectedUrl_TrailingSlashDifference_ReturnsFalse()
    {
        // Normalized URLs should be equal regardless of trailing slash
        BackgroundPreloadService.IsRedirectedUrl(
            "https://example.com/page",
            "https://example.com/page/").Should().BeFalse();
    }

    [Fact]
    public void IsRedirectedUrl_TrackingParamsDifference_ReturnsFalse()
    {
        // Different tracking parameters should normalize to same URL
        BackgroundPreloadService.IsRedirectedUrl(
            "https://example.com/article?utm_source=twitter",
            "https://example.com/article?utm_source=google").Should().BeFalse();
    }

    [Fact]
    public void IsRedirectedUrl_NullFinalUrl_ReturnsFalse()
    {
        BackgroundPreloadService.IsRedirectedUrl(
            "https://example.com/page", null!).Should().BeFalse();
    }

    [Fact]
    public void IsRedirectedUrl_EmptyFinalUrl_ReturnsFalse()
    {
        BackgroundPreloadService.IsRedirectedUrl(
            "https://example.com/page", "").Should().BeFalse();
    }

    #endregion

    #region Pause / Resume

    [Fact]
    public async Task StartAsync_WhenPaused_SkipsPreloading()
    {
        _idleDetector.WaitForIdleAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var nodes = CreateContentNodes("https://example.com/a1");
        _service.NotifySelectionChanged(0, nodes, "https://example.com");

        _service.Pause();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await _service.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Should not have tried to cache anything since paused
        _cache.DidNotReceive().Put(Arg.Any<string>(), Arg.Any<Application.DTOs.Browser.PageLoadResult>());
    }

    #endregion

    #region NotifySelectionChanged

    [Fact]
    public async Task NotifySelectionChanged_DebouncesQueueRebuilds()
    {
        var nodes1 = CreateContentNodes("https://example.com/a1");
        var nodes2 = CreateContentNodes("https://example.com/b1", "https://example.com/b2");

        // Rapid-fire changes should be debounced — only the last one should take effect
        _service.NotifySelectionChanged(0, nodes1, "https://example.com");
        _service.NotifySelectionChanged(0, nodes2, "https://example.com");

        // Wait for debounce timer to fire (200ms + margin)
        await Task.Delay(350);

        // Read internal _queue via reflection to verify actual debounce behavior
        var queueField = typeof(BackgroundPreloadService)
            .GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var internalQueue = (List<BackgroundPreloadService.PreloadItem>)queueField!.GetValue(_service)!;

        // Should reflect the second (latest) call — 2 items from nodes2
        internalQueue.Should().HaveCount(2);
        internalQueue.Select(i => i.Url).Should().Contain("https://example.com/b1");
        internalQueue.Select(i => i.Url).Should().Contain("https://example.com/b2");
    }

    [Fact]
    public void NotifySelectionChanged_AfterDispose_DoesNotThrow()
    {
        _service.Dispose();

        var nodes = CreateContentNodes("https://example.com/a1");

        var act = () => _service.NotifySelectionChanged(0, nodes, "https://example.com");
        act.Should().NotThrow();
    }

    #endregion

    #region Circuit Breaker Recovery

    [Fact]
    public void BuildQueue_CircuitBrokenDomain_SkipsDuringCooldown()
    {
        // Simulate bot detection for example.com by building a service that
        // has the domain circuit-broken via reflection (since the internal
        // dictionary isn't directly accessible from tests, we use the
        // BuildQueue skip behavior after a prior failed fetch).
        // Instead, we can trigger the skip by using a short cooldown config.
        var cache = Substitute.For<IPageCache>();
        var config = new CacheConfiguration { CircuitBreakerCooldownSeconds = 300 };
        using var service = new BackgroundPreloadService(
            cache, Substitute.For<IIdleDetector>(), new HttpClient(), config,
            NullLogger<BackgroundPreloadService>.Instance);

        // Break the circuit by accessing the internal dictionary via reflection
        var field = typeof(BackgroundPreloadService)
            .GetField("_circuitBrokenDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, DateTime>)field!.GetValue(service)!;
        dict["https://example.com"] = DateTime.UtcNow;

        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");

        var queue = service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().BeEmpty("domain is circuit-broken and cooldown has not elapsed");

        // Verify circuit-broken URLs are counted in NeedsBrowserCount (not silently dropped)
        var progress = service.GetProgress();
        progress.NeedsBrowserCount.Should().Be(2,
            "circuit-broken URLs should be counted as needing browser so progress completes");
        progress.TotalCacheableLinks.Should().Be(2);
    }

    [Fact]
    public void BuildQueue_CircuitBrokenDomain_RecoverAfterCooldown()
    {
        var cache = Substitute.For<IPageCache>();
        var config = new CacheConfiguration { CircuitBreakerCooldownSeconds = 1 };
        using var service = new BackgroundPreloadService(
            cache, Substitute.For<IIdleDetector>(), new HttpClient(), config,
            NullLogger<BackgroundPreloadService>.Instance);

        // Break the circuit with a time in the past (beyond cooldown)
        var field = typeof(BackgroundPreloadService)
            .GetField("_circuitBrokenDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, DateTime>)field!.GetValue(service)!;
        dict["https://example.com"] = DateTime.UtcNow.AddSeconds(-5);

        var nodes = CreateContentNodes("https://example.com/a1");

        var queue = service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().HaveCount(1, "cooldown has elapsed, domain should be retried");
        dict.Should().NotContainKey("https://example.com", "expired circuit breaker entry should be removed");
    }

    #endregion

    #region Needs-JS Domain Tracking

    [Fact]
    public void BuildQueue_NeedsJsDomain_SkipsUrls()
    {
        var cache = Substitute.For<IPageCache>();
        using var service = new BackgroundPreloadService(
            cache, Substitute.For<IIdleDetector>(), new HttpClient(), new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance);

        // Mark domain as needing JS via reflection
        var field = typeof(BackgroundPreloadService)
            .GetField("_needsJsDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, bool>)field!.GetValue(service)!;
        dict["https://example.com"] = true;

        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");

        var queue = service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().BeEmpty("domain needs JS rendering, HTTP pre-loads should be skipped");
    }

    [Fact]
    public void BuildQueue_NeedsJsDomain_DoesNotAffectOtherDomains()
    {
        var cache = Substitute.For<IPageCache>();
        using var service = new BackgroundPreloadService(
            cache, Substitute.For<IIdleDetector>(), new HttpClient(), new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance);

        // Mark only one domain as needing JS
        var field = typeof(BackgroundPreloadService)
            .GetField("_needsJsDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, bool>)field!.GetValue(service)!;
        dict["https://jsonly.com"] = true;

        // URLs from a different domain should still be queued
        var nodes = CreateContentNodes("https://example.com/a1");

        var queue = service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().HaveCount(1);
    }

    #endregion

    #region Content Quality Gate

    [Fact]
    public void HasSufficientContent_UsedByPreloadService_RejectsEmptyPages()
    {
        // Verifies that the same HasSufficientContent check used by CachingPageLoader
        // is accessible and works for the preload service's quality gate
        var emptyHtml = "<html><body><p>short</p></body></html>";
        CachingPageLoader.HasSufficientContent(emptyHtml).Should().BeFalse();
    }

    [Fact]
    public void HasSufficientContent_UsedByPreloadService_AcceptsRichContent()
    {
        var richHtml = "<html><body>" +
            string.Join(" ", Enumerable.Range(1, 100).Select(i => $"<p>This is paragraph number {i} with enough words to be substantial content.</p>")) +
            "</body></html>";
        CachingPageLoader.HasSufficientContent(richHtml).Should().BeTrue();
    }

    [Fact]
    public void HasSufficientContent_JsShellWithoutArticleMarkup_ReturnsFalse()
    {
        // JS shell pages without article indicators pass IsEmptyArticleShell
        // but should be caught by HasSufficientContent
        var jsShellHtml = @"<html>
            <head><title>App</title></head>
            <body>
                <div id='root'></div>
                <script src='/app.js'></script>
                <noscript>Enable JavaScript</noscript>
            </body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(jsShellHtml).Should().BeFalse(
            "no article indicators, so IsEmptyArticleShell does not flag it");
        CachingPageLoader.HasSufficientContent(jsShellHtml).Should().BeFalse(
            "HasSufficientContent catches it as a quality gate");
    }

    #endregion

    #region GetProgress

    [Fact]
    public void GetProgress_NoLinks_ReturnsZeros()
    {
        var progress = _service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(0);
        progress.CachedCount.Should().Be(0);
        progress.NeedsBrowserCount.Should().Be(0);
        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void GetProgress_AfterBuildQueue_ReportsEligibleLinks()
    {
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2", "https://example.com/a3");
        _service.BuildQueue(0, nodes, "https://example.com");

        var progress = _service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(3);
        progress.CachedCount.Should().Be(0);
        progress.NeedsBrowserCount.Should().Be(0);
        progress.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void GetProgress_WithCachedLinks_ReportsCachedCount()
    {
        _cache.Contains("https://example.com/a1").Returns(true);
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");
        _service.BuildQueue(0, nodes, "https://example.com");

        var progress = _service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(2);
        progress.CachedCount.Should().Be(1);
        progress.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void GetProgress_WithNeedsJsDomains_ReportsNeedsBrowserCount()
    {
        var field = typeof(BackgroundPreloadService)
            .GetField("_needsJsDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, bool>)field!.GetValue(_service)!;
        dict["https://example.com"] = true;

        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");
        _service.BuildQueue(0, nodes, "https://example.com");

        var progress = _service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(2);
        progress.NeedsBrowserCount.Should().Be(2);
        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void GetProgress_AllCached_IsComplete()
    {
        _cache.Contains(Arg.Any<string>()).Returns(true);
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");
        _service.BuildQueue(0, nodes, "https://example.com");

        var progress = _service.GetProgress();

        progress.IsComplete.Should().BeTrue();
    }

    #endregion

    #region BuildCollectionQueue

    [Fact]
    public void BuildCollectionQueue_SortedByProximityToSelected()
    {
        var urls = new List<string>
        {
            "https://a.com/1",
            "https://b.com/2",
            "https://c.com/3",
            "https://d.com/4",
            "https://e.com/5",
        };

        var queue = _service.BuildCollectionQueue(2, urls);

        // Selected index is 2, so item at index 2 (distance 0) should come first
        queue[0].Url.Should().Be("https://c.com/3");
    }

    [Fact]
    public void BuildCollectionQueue_AllowsCrossOriginUrls()
    {
        // Unlike BuildQueue, collection queue has no same-origin filter
        var urls = new List<string>
        {
            "https://site-a.com/article",
            "https://site-b.com/article",
            "https://site-c.com/article",
        };

        var queue = _service.BuildCollectionQueue(0, urls);

        queue.Should().HaveCount(3);
        queue.Select(i => i.Url).Should().Contain("https://site-a.com/article");
        queue.Select(i => i.Url).Should().Contain("https://site-b.com/article");
        queue.Select(i => i.Url).Should().Contain("https://site-c.com/article");
    }

    [Fact]
    public void BuildCollectionQueue_SkipsCachedUrls()
    {
        _cache.Contains("https://cached.com/article").Returns(true);

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
    public void BuildCollectionQueue_SkipsCircuitBrokenDomains()
    {
        // Break the circuit for a domain via reflection
        var field = typeof(BackgroundPreloadService)
            .GetField("_circuitBrokenDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, DateTime>)field!.GetValue(_service)!;
        dict["https://broken.com"] = DateTime.UtcNow;

        var urls = new List<string>
        {
            "https://broken.com/article",
            "https://working.com/article",
        };

        var queue = _service.BuildCollectionQueue(0, urls);

        queue.Should().ContainSingle();
        queue[0].Url.Should().Be("https://working.com/article");
    }

    [Fact]
    public void BuildCollectionQueue_SkipsNeedsJsDomains()
    {
        var field = typeof(BackgroundPreloadService)
            .GetField("_needsJsDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, bool>)field!.GetValue(_service)!;
        dict["https://jsonly.com"] = true;

        var urls = new List<string>
        {
            "https://jsonly.com/article",
            "https://static.com/article",
        };

        var queue = _service.BuildCollectionQueue(0, urls);

        queue.Should().ContainSingle();
        queue[0].Url.Should().Be("https://static.com/article");
    }

    [Fact]
    public void BuildCollectionQueue_EmptyUrls_ReturnsEmpty()
    {
        var queue = _service.BuildCollectionQueue(0, new List<string>());
        queue.Should().BeEmpty();
    }

    [Fact]
    public void BuildCollectionQueue_SkipsEmptyUrls()
    {
        var urls = new List<string> { "", "https://valid.com/article", null! };

        var queue = _service.BuildCollectionQueue(0, urls);

        queue.Should().ContainSingle();
        queue[0].Url.Should().Be("https://valid.com/article");
    }

    [Fact]
    public void BuildCollectionQueue_UpdatesProgressTracking()
    {
        var urls = new List<string>
        {
            "https://a.com/1",
            "https://b.com/2",
        };

        _service.BuildCollectionQueue(0, urls);

        var progress = _service.GetProgress();
        progress.TotalCacheableLinks.Should().Be(2);
    }

    #endregion

    #region ClearQueue

    [Fact]
    public void ClearQueue_EmptiesQueueAndProgress()
    {
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");
        _service.BuildQueue(0, nodes, "https://example.com");

        _service.GetProgress().TotalCacheableLinks.Should().Be(2);

        _service.ClearQueue();

        _service.GetProgress().TotalCacheableLinks.Should().Be(0);
        _service.GetProgress().CachedCount.Should().Be(0);
        _service.GetProgress().NeedsBrowserCount.Should().Be(0);
    }

    #endregion

    #region Debounce: Collection vs Link-Tree Mode

    [Fact]
    public async Task NotifyCollectionChanged_DebouncesAndBuildsCollectionQueue()
    {
        var urls1 = new List<string> { "https://a.com/1" };
        var urls2 = new List<string> { "https://b.com/1", "https://c.com/2", "https://d.com/3" };

        // Rapid-fire collection changes
        _service.NotifyCollectionChanged(0, urls1);
        _service.NotifyCollectionChanged(0, urls2);

        // Wait for debounce (200ms + margin)
        await Task.Delay(350);

        // Should reflect the latest call
        var queueField = typeof(BackgroundPreloadService)
            .GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var internalQueue = (List<BackgroundPreloadService.PreloadItem>)queueField!.GetValue(_service)!;

        internalQueue.Should().HaveCount(3);
    }

    [Fact]
    public async Task Debounce_CollectionOverridesLinkTree()
    {
        // First: link-tree notification
        var nodes = CreateContentNodes("https://example.com/a1");
        _service.NotifySelectionChanged(0, nodes, "https://example.com");

        // Then immediately: collection notification (should override)
        var urls = new List<string> { "https://col.com/1", "https://col.com/2" };
        _service.NotifyCollectionChanged(0, urls);

        await Task.Delay(350);

        var queueField = typeof(BackgroundPreloadService)
            .GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var internalQueue = (List<BackgroundPreloadService.PreloadItem>)queueField!.GetValue(_service)!;

        // Should contain collection URLs, not link-tree
        internalQueue.Should().HaveCount(2);
        internalQueue.Select(i => i.Url).Should().Contain("https://col.com/1");
    }

    [Fact]
    public async Task Debounce_LinkTreeOverridesCollection()
    {
        // First: collection notification
        var urls = new List<string> { "https://col.com/1", "https://col.com/2" };
        _service.NotifyCollectionChanged(0, urls);

        // Then immediately: link-tree notification (should override)
        var nodes = CreateContentNodes("https://example.com/a1");
        _service.NotifySelectionChanged(0, nodes, "https://example.com");

        await Task.Delay(350);

        var queueField = typeof(BackgroundPreloadService)
            .GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var internalQueue = (List<BackgroundPreloadService.PreloadItem>)queueField!.GetValue(_service)!;

        // Should contain link-tree URL, not collection
        internalQueue.Should().ContainSingle();
        internalQueue[0].Url.Should().Be("https://example.com/a1");
    }

    #endregion

    #region Adaptive Rate Limiting

    [Fact]
    public void GetAdaptiveDelay_Disabled_ReturnsFullDelay()
    {
        var config = new CacheConfiguration { AdaptiveRateLimitEnabled = false, PreloadDelayMs = 4000 };
        var service = CreateService(config);

        var delay = service.GetAdaptiveDelay("https://example.com/a1");

        delay.Should().Be(4000);
    }

    [Fact]
    public void GetAdaptiveDelay_EmptyQueue_ReturnsFullDelay()
    {
        var config = new CacheConfiguration { AdaptiveRateLimitEnabled = true, PreloadDelayMs = 4000 };
        var service = CreateService(config);

        // No items in queue
        var delay = service.GetAdaptiveDelay("https://example.com/a1");

        delay.Should().Be(4000);
    }

    [Fact]
    public void GetAdaptiveDelay_SameDomainNext_ReturnsFullDelay()
    {
        var config = new CacheConfiguration
        {
            AdaptiveRateLimitEnabled = true,
            PreloadDelayMs = 4000,
            CrossDomainDelayMs = 1500,
        };
        var service = CreateService(config);

        // Queue up same-domain items
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2");
        service.BuildQueue(0, nodes, "https://example.com");

        // Simulate having fetched a1, so a2 (same domain) is next
        var delay = service.GetAdaptiveDelay("https://example.com/a1");

        delay.Should().Be(4000);
    }

    [Fact]
    public void GetAdaptiveDelay_DifferentDomainNext_ReturnsCrossDomainDelay()
    {
        var config = new CacheConfiguration
        {
            AdaptiveRateLimitEnabled = true,
            PreloadDelayMs = 4000,
            CrossDomainDelayMs = 1500,
        };
        var service = CreateService(config);

        // Build collection queue — simulate that other.com/b1 was already dequeued,
        // so the next item in queue is another.com/c1 (different domain)
        var urls = new List<string> { "https://other.com/b1", "https://another.com/c1" };
        var queue = service.BuildCollectionQueue(0, urls);
        queue.RemoveAll(i => i.Url == "https://other.com/b1"); // Already fetched
        SetInternalQueue(service, queue);

        // After fetching from other.com, next is another.com (different domain)
        var delay = service.GetAdaptiveDelay("https://other.com/b1");

        delay.Should().Be(1500);
    }

    [Fact]
    public void GetAdaptiveDelay_DifferentDomainButRecentlyHit_ReturnsRemainingCooldown()
    {
        var config = new CacheConfiguration
        {
            AdaptiveRateLimitEnabled = true,
            PreloadDelayMs = 4000,
            CrossDomainDelayMs = 1500,
        };
        var service = CreateService(config);

        // Pre-populate lastRequestByDomain for next domain via reflection
        var lastRequestField = typeof(BackgroundPreloadService)
            .GetField("_lastRequestByDomain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, DateTime>)lastRequestField!.GetValue(service)!;
        // Simulate that we hit "https://another.com" 1 second ago
        dict["https://another.com"] = DateTime.UtcNow.AddMilliseconds(-1000);

        // Queue with another.com as next
        var urls = new List<string> { "https://another.com/c1" };
        var queue = service.BuildCollectionQueue(0, urls);
        SetInternalQueue(service, queue);

        var delay = service.GetAdaptiveDelay("https://other.com/b1");

        // Remaining cooldown: 4000 - 1000 = 3000, which is > 1500 (CrossDomainDelayMs)
        delay.Should().BeInRange(2500, 3500, "should respect per-domain cooldown");
    }

    [Fact]
    public void GetAdaptiveDelay_RecordsDomainTimestamp()
    {
        var config = new CacheConfiguration { AdaptiveRateLimitEnabled = true };
        var service = CreateService(config);

        // Build queue so there's something next
        var urls = new List<string> { "https://other.com/b1" };
        service.BuildCollectionQueue(0, urls);

        service.GetAdaptiveDelay("https://example.com/a1");

        var field = typeof(BackgroundPreloadService)
            .GetField("_lastRequestByDomain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, DateTime>)field!.GetValue(service)!;
        dict.Should().ContainKey("https://example.com");
    }

    #endregion

    #region Preload Budget Limit

    [Fact]
    public void BuildQueue_BudgetLimit_TruncatesQueueToMaxPreloadLinks()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 3 };
        var service = CreateService(config);

        // Create 10 same-origin content links
        var urls = Enumerable.Range(1, 10).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        var queue = service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().HaveCount(3, "queue should be truncated to MaxPreloadLinks");
    }

    [Fact]
    public void BuildQueue_BudgetLimit_KeepsHighestPriorityItems()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 3 };
        var service = CreateService(config);

        var urls = Enumerable.Range(1, 10).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        // Selected index 0 — items closest to cursor (indices 0, 1, 2) should be kept
        var queue = service.BuildQueue(0, nodes, "https://example.com");

        queue.Select(i => i.ListIndex).Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public void BuildQueue_BudgetLimit_ProgressReflectsBudget()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 3 };
        var service = CreateService(config);

        var urls = Enumerable.Range(1, 10).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        service.BuildQueue(0, nodes, "https://example.com");
        var progress = service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(3, "progress should reflect the budget, not all eligible URLs");
    }

    [Fact]
    public void BuildQueue_UnderBudget_NoTruncation()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 20 };
        var service = CreateService(config);

        var urls = Enumerable.Range(1, 5).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        var queue = service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().HaveCount(5, "should not truncate when under budget");
    }

    [Fact]
    public void BuildCollectionQueue_BudgetLimit_TruncatesQueueToMaxPreloadLinks()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 3 };
        var service = CreateService(config);

        var urls = Enumerable.Range(1, 10).Select(i => $"https://site{i}.com/article").ToList();

        var queue = service.BuildCollectionQueue(0, urls);

        queue.Should().HaveCount(3, "collection queue should be truncated to MaxPreloadLinks");
    }

    [Fact]
    public void BuildCollectionQueue_BudgetLimit_KeepsClosestToSelected()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 3 };
        var service = CreateService(config);

        var urls = Enumerable.Range(1, 10).Select(i => $"https://site{i}.com/article").ToList();

        // Selected index 4 — items at indices 4, 3, 5 should be kept (closest first)
        var queue = service.BuildCollectionQueue(4, urls);

        queue.Select(i => i.ListIndex).Should().BeEquivalentTo(new[] { 3, 4, 5 });
    }

    [Fact]
    public void BuildCollectionQueue_BudgetLimit_ProgressReflectsBudget()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 3 };
        var service = CreateService(config);

        var urls = Enumerable.Range(1, 10).Select(i => $"https://site{i}.com/article").ToList();

        service.BuildCollectionQueue(0, urls);
        var progress = service.GetProgress();

        progress.TotalCacheableLinks.Should().Be(3, "collection progress should reflect the budget");
    }

    [Fact]
    public void BuildCollectionQueue_UnderBudget_NoTruncation()
    {
        var config = new CacheConfiguration { MaxPreloadLinks = 20 };
        var service = CreateService(config);

        var urls = Enumerable.Range(1, 5).Select(i => $"https://site{i}.com/article").ToList();

        var queue = service.BuildCollectionQueue(0, urls);

        queue.Should().HaveCount(5, "should not truncate when under budget");
    }

    [Fact]
    public void BuildQueue_DefaultBudget_Is50()
    {
        var config = new CacheConfiguration();

        config.MaxPreloadLinks.Should().Be(50);
    }

    #endregion

    #region Helpers

    private static void SetInternalQueue(BackgroundPreloadService service, List<BackgroundPreloadService.PreloadItem> queue)
    {
        var field = typeof(BackgroundPreloadService)
            .GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(service, queue);
    }

    private static BackgroundPreloadService CreateService(CacheConfiguration config)
    {
        return new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            config,
            NullLogger<BackgroundPreloadService>.Instance);
    }

    private static List<LinkNode> CreateContentNodes(params string[] urls)
    {
        var root = LinkNode.CreateRoot();
        foreach (var url in urls)
        {
            var link = new LinkInfo
            {
                Url = url,
                DisplayText = $"Article at {url}",
                Type = LinkType.Content,
                ImportanceScore = 50
            };
            root.AddChild(link);
        }

        return root.Children.ToList();
    }

    #endregion

    #region WaitForInFlightAsync

    [Fact]
    public async Task WaitForInFlightAsync_NoInFlight_ReturnsNull()
    {
        var result = await _service.WaitForInFlightAsync(
            "https://example.com/page1",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task WaitForInFlightAsync_TimesOut_ReturnsNull()
    {
        // Use reflection to inject a long-running task into _inFlight
        var inFlight = GetInFlightDictionary();
        var tcs = new TaskCompletionSource<PageLoadResult>();
        inFlight["https://example.com/slow"] = tcs.Task;

        var result = await _service.WaitForInFlightAsync(
            "https://example.com/slow",
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        result.Should().BeNull();

        // Cleanup
        tcs.TrySetCanceled();
    }

    [Fact]
    public async Task WaitForInFlightAsync_FaultedInFlight_ReturnsNull()
    {
        var inFlight = GetInFlightDictionary();
        var tcs = new TaskCompletionSource<PageLoadResult>();
        tcs.SetException(new HttpRequestException("Connection refused"));
        inFlight["https://example.com/error"] = tcs.Task;

        var result = await _service.WaitForInFlightAsync(
            "https://example.com/error",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task WaitForInFlightAsync_CompletedInFlight_ReturnsResult()
    {
        var inFlight = GetInFlightDictionary();
        var expected = PageLoadResult.Successful(
            "https://example.com/ready",
            "<html>content</html>",
            new PageMetadata { Title = "Ready" });

        inFlight["https://example.com/ready"] = Task.FromResult(expected);

        var result = await _service.WaitForInFlightAsync(
            "https://example.com/ready",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://example.com/ready");
    }

    private ConcurrentDictionary<string, Task<PageLoadResult>> GetInFlightDictionary()
    {
        var field = typeof(BackgroundPreloadService)
            .GetField("_inFlight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (ConcurrentDictionary<string, Task<PageLoadResult>>)field!.GetValue(_service)!;
    }

    #endregion

    #region Article Content Extraction During Preload

    [Fact]
    public async Task PreloadUrl_ArticlePage_ExtractsAndCachesArticleContent()
    {
        // Arrange
        var articleContentCache = Substitute.For<IArticleContentCache>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();

        var url = "https://example.com/article1";
        var html = "<html><article><p>Article content with enough text to be substantial.</p></article></html>";

        contentExtractor.IsArticle(html).Returns(true);
        contentExtractor.ExtractAsync(html, url, Arg.Any<CancellationToken>())
            .Returns(ReadableContent.Create(
                "Test Article",
                "Article content with enough text to be substantial.",
                new List<string> { "Article content with enough text to be substantial." },
                "Jane Doe",
                new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));

        var service = CreateServiceWithArticleExtraction(articleContentCache, contentExtractor);

        // Act — call TryExtractAndCacheArticleAsync via reflection (private method)
        var method = typeof(BackgroundPreloadService)
            .GetMethod("TryExtractAndCacheArticleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, new object[] { url, html, CancellationToken.None })!;

        // Assert
        await articleContentCache.Received(1).PutAsync(
            url,
            Arg.Is<ExtractedArticle>(a =>
                a.Title == "Test Article" &&
                a.CleanedText == "Article content with enough text to be substantial." &&
                a.Author == "Jane Doe" &&
                a.Url == url &&
                a.PublishedDate == new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreloadUrl_NonArticlePage_SkipsCachePut()
    {
        // Arrange
        var articleContentCache = Substitute.For<IArticleContentCache>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();

        var url = "https://example.com/index";
        var html = "<html><body><nav>Navigation links</nav></body></html>";

        // ExtractAsync returns null for non-article content
        contentExtractor.ExtractAsync(html, url, Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        var service = CreateServiceWithArticleExtraction(articleContentCache, contentExtractor);

        // Act
        var method = typeof(BackgroundPreloadService)
            .GetMethod("TryExtractAndCacheArticleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, new object[] { url, html, CancellationToken.None })!;

        // Assert — ExtractAsync was called but PutAsync should not (null result)
        await contentExtractor.Received(1).ExtractAsync(html, url, Arg.Any<CancellationToken>());
        await articleContentCache.DidNotReceive().PutAsync(Arg.Any<string>(), Arg.Any<ExtractedArticle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreloadUrl_ExtractionReturnsNull_SkipsCachePut()
    {
        // Arrange
        var articleContentCache = Substitute.For<IArticleContentCache>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();

        var url = "https://example.com/article";
        var html = "<html><article>Too short</article></html>";

        contentExtractor.IsArticle(html).Returns(true);
        contentExtractor.ExtractAsync(html, url, Arg.Any<CancellationToken>())
            .Returns((ReadableContent?)null);

        var service = CreateServiceWithArticleExtraction(articleContentCache, contentExtractor);

        // Act
        var method = typeof(BackgroundPreloadService)
            .GetMethod("TryExtractAndCacheArticleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, new object[] { url, html, CancellationToken.None })!;

        // Assert
        await articleContentCache.DidNotReceive().PutAsync(Arg.Any<string>(), Arg.Any<ExtractedArticle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreloadUrl_ExtractionThrows_DoesNotBreakPreload()
    {
        // Arrange
        var articleContentCache = Substitute.For<IArticleContentCache>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();

        var url = "https://example.com/article";
        var html = "<html><article><p>Content</p></article></html>";

        contentExtractor.IsArticle(html).Returns(true);
        contentExtractor.ExtractAsync(html, url, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Extraction failed"));

        var service = CreateServiceWithArticleExtraction(articleContentCache, contentExtractor);

        // Act — should not throw
        var method = typeof(BackgroundPreloadService)
            .GetMethod("TryExtractAndCacheArticleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var act = async () => await (Task)method!.Invoke(service, new object[] { url, html, CancellationToken.None })!;

        // Assert
        await act.Should().NotThrowAsync();
        await articleContentCache.DidNotReceive().PutAsync(Arg.Any<string>(), Arg.Any<ExtractedArticle>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreloadUrl_CachePutThrows_DoesNotBreakPreload()
    {
        // Arrange
        var articleContentCache = Substitute.For<IArticleContentCache>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();

        var url = "https://example.com/article";
        var html = "<html><article><p>Content paragraph.</p></article></html>";

        contentExtractor.IsArticle(html).Returns(true);
        contentExtractor.ExtractAsync(html, url, Arg.Any<CancellationToken>())
            .Returns(ReadableContent.Create(
                "Title",
                "Content paragraph.",
                new List<string> { "Content paragraph." }));

        articleContentCache.PutAsync(Arg.Any<string>(), Arg.Any<ExtractedArticle>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("Disk full"));

        var service = CreateServiceWithArticleExtraction(articleContentCache, contentExtractor);

        // Act
        var method = typeof(BackgroundPreloadService)
            .GetMethod("TryExtractAndCacheArticleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var act = async () => await (Task)method!.Invoke(service, new object[] { url, html, CancellationToken.None })!;

        // Assert — error swallowed, does not propagate
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PreloadUrl_NullDependencies_SkipsArticleExtraction()
    {
        // Arrange — service without article extraction dependencies (default constructor)
        var service = CreateService(new CacheConfiguration());

        // Act — should not throw even though articleContentCache and contentExtractor are null
        var method = typeof(BackgroundPreloadService)
            .GetMethod("TryExtractAndCacheArticleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var act = async () => await (Task)method!.Invoke(service, new object[] { "https://example.com/article", "<html></html>", CancellationToken.None })!;

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PreloadUrl_ArticleExtraction_PreservesWordCount()
    {
        // Arrange
        var articleContentCache = Substitute.For<IArticleContentCache>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();

        var url = "https://example.com/article";
        var html = "<html><article><p>Word one two three four five six seven eight nine ten.</p></article></html>";
        var cleanedText = "Word one two three four five six seven eight nine ten.";

        contentExtractor.IsArticle(html).Returns(true);
        contentExtractor.ExtractAsync(html, url, Arg.Any<CancellationToken>())
            .Returns(ReadableContent.Create(
                "Word Count Test",
                cleanedText,
                new List<string> { cleanedText }));

        var service = CreateServiceWithArticleExtraction(articleContentCache, contentExtractor);

        // Act
        var method = typeof(BackgroundPreloadService)
            .GetMethod("TryExtractAndCacheArticleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, new object[] { url, html, CancellationToken.None })!;

        // Assert — word count from ReadableContent should be forwarded
        await articleContentCache.Received(1).PutAsync(
            url,
            Arg.Is<ExtractedArticle>(a => a.WordCount > 0 && a.Title == "Word Count Test"),
            Arg.Any<CancellationToken>());
    }

    private static BackgroundPreloadService CreateServiceWithArticleExtraction(
        IArticleContentCache articleContentCache,
        IReadableContentExtractor contentExtractor)
    {
        return new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            contentExtractor,
            linkExtractor: null,
            articleContentCache);
    }

    #endregion

    #region Content Validation Before Caching

    /// <summary>
    /// Generates HTML that looks like an article (has article indicators) with substantial
    /// extractable content (3+ paragraphs with >50 chars each in a content area).
    /// </summary>
    private static string BuildGoodArticleHtml()
    {
        var p1 = "This is the first paragraph of the article with plenty of content to read through and understand.";
        var p2 = "The second paragraph continues the story with additional details and information for readers to enjoy.";
        var p3 = "A third paragraph wraps up the section with concluding thoughts and final remarks for the audience.";
        return $@"<html><head><meta property='og:type' content='article' /><title>Good Article</title></head>
            <body><article>
                <h1>Good Article Title</h1>
                <p>{p1}</p>
                <p>{p2}</p>
                <p>{p3}</p>
            </article></body></html>";
    }

    /// <summary>
    /// Generates HTML that looks like an article (has article indicators) but has NO
    /// extractable content — only navigation/boilerplate text. Simulates a JS-heavy
    /// site that returns a shell page via HTTP.
    /// </summary>
    private static string BuildJsShellArticleHtml()
    {
        // Has article indicators (og:type=article) and enough total words in nav/header
        // to pass HasSufficientContent, but no actual article paragraphs in content areas.
        return @"<html><head><meta property='og:type' content='article' /><title>JS Shell</title></head>
            <body>
                <nav>
                    <a href='/section1'>Section 1</a>
                    <a href='/section2'>Section 2</a>
                    <a href='/section3'>Section 3</a>
                    <a href='/section4'>Section 4</a>
                    <a href='/section5'>Section 5</a>
                </nav>
                <div id='app-root'>
                    <!-- Content will be rendered by JavaScript -->
                </div>
                <footer>
                    <p>Copyright 2026 Example News. All rights reserved. Terms of Service apply to all content.</p>
                    <p>Contact us at editor@example.com for inquiries about subscriptions and advertising rates.</p>
                </footer>
            </body></html>";
    }

    [Fact]
    public void IsArticlePage_GoodArticleHtml_ReturnsTrue()
    {
        var html = BuildGoodArticleHtml();
        ReadableContentExtractor.IsArticlePage(html).Should().BeTrue();
    }

    [Fact]
    public void IsArticlePage_JsShellArticleHtml_ReturnsTrue()
    {
        // Even the shell page has article indicators (og:type=article)
        var html = BuildJsShellArticleHtml();
        ReadableContentExtractor.IsArticlePage(html).Should().BeTrue();
    }

    [Fact]
    public void HasExtractableContent_GoodArticleHtml_ReturnsTrue()
    {
        var html = BuildGoodArticleHtml();
        ReadableContentExtractor.HasExtractableContent(html).Should().BeTrue(
            "HTML with article tag and 3+ substantial paragraphs should have extractable content");
    }

    [Fact]
    public void HasExtractableContent_JsShellArticleHtml_ReturnsFalse()
    {
        var html = BuildJsShellArticleHtml();
        ReadableContentExtractor.HasExtractableContent(html).Should().BeFalse(
            "JS shell page with only nav/footer text should NOT have extractable content");
    }

    [Fact]
    public void HasExtractableContent_EmptyHtml_ReturnsFalse()
    {
        ReadableContentExtractor.HasExtractableContent("").Should().BeFalse();
        ReadableContentExtractor.HasExtractableContent("   ").Should().BeFalse();
    }

    [Fact]
    public void HasExtractableContent_NonArticlePlainPage_ReturnsFalse()
    {
        // Simple page with no article structure
        var html = "<html><body><p>Just some text</p></body></html>";
        ReadableContentExtractor.HasExtractableContent(html).Should().BeFalse(
            "a page without content area selectors and too few paragraphs has no extractable content");
    }

    [Fact]
    public void HasExtractableContent_ArticleWithEntryContent_ReturnsTrue()
    {
        var p1 = "This is the first paragraph of the article with plenty of content to read through carefully.";
        var p2 = "The second paragraph continues with additional details and information for readers to enjoy fully.";
        var p3 = "A third paragraph wraps up the section with concluding thoughts and final remarks for everyone.";
        var html = $@"<html><body>
            <div class='entry-content'>
                <p>{p1}</p>
                <p>{p2}</p>
                <p>{p3}</p>
            </div>
        </body></html>";

        ReadableContentExtractor.HasExtractableContent(html).Should().BeTrue(
            "page with entry-content div containing 3+ substantial paragraphs should be extractable");
    }

    [Fact]
    public void HasExtractableContent_ArticleBodyInMainRole_ReturnsTrue()
    {
        var p1 = "This is the first paragraph of the article with plenty of content to read through carefully.";
        var p2 = "The second paragraph continues with additional details and information for readers to enjoy fully.";
        var p3 = "A third paragraph wraps up the section with concluding thoughts and final remarks for everyone.";
        var html = $@"<html><body>
            <div role='main'>
                <p>{p1}</p>
                <p>{p2}</p>
                <p>{p3}</p>
            </div>
        </body></html>";

        ReadableContentExtractor.HasExtractableContent(html).Should().BeTrue(
            "page with role=main containing 3+ substantial paragraphs should be extractable");
    }

    #endregion

    #region Paywalled Domain Content Quality Gate

    [Fact]
    public void HasSufficientContent_PaywalledThreshold_RejectsTruncatedPreview()
    {
        // Simulate a truncated paywall preview (~200 words)
        var words = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"word{i}"));
        var html = $"<html><body><article><p>{words}</p></article></body></html>";

        // Passes the general 50-word threshold
        CachingPageLoader.HasSufficientContent(html, 50).Should().BeTrue();

        // Fails the paywalled domain threshold (500 words)
        CachingPageLoader.HasSufficientContent(html, 500).Should().BeFalse(
            "truncated paywall preview with ~200 words should not pass the 500-word paywalled threshold");
    }

    [Fact]
    public void HasSufficientContent_PaywalledThreshold_AcceptsFullArticle()
    {
        // Simulate a full article (600+ words)
        var words = string.Join(" ", Enumerable.Range(1, 600).Select(i => $"word{i}"));
        var html = $"<html><body><article><p>{words}</p></article></body></html>";

        CachingPageLoader.HasSufficientContent(html, 500).Should().BeTrue(
            "full article with 600+ words should pass the 500-word paywalled threshold");
    }

    [Fact]
    public void BuildQueue_PaywalledDomainWithCookies_IncludesInQueue()
    {
        var browserConfig = new BrowserConfiguration { PaywalledDomains = ["nytimes.com"] };
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.GetCookieInfoAsync().Returns(new CookieInfo { Exists = true, IsExpired = false });

        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            browserConfig: browserConfig,
            cookieManager: cookieManager);

        var nodes = CreateContentNodes(
            "https://www.nytimes.com/2026/01/article.html");

        // Trigger cookie state refresh via the debounce path indirectly —
        // call RefreshPaywalledCookieState via reflection
        var refreshMethod = typeof(BackgroundPreloadService)
            .GetMethod("RefreshPaywalledCookieState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        refreshMethod!.Invoke(service, null);

        var queue = service.BuildQueue(0, nodes, "https://www.nytimes.com");

        queue.Should().HaveCount(1, "paywalled domain with valid cookies should be queued for preloading");
    }

    [Fact]
    public void RefreshPaywalledCookieState_ClearsNeedsJsWhenCookiesAppear()
    {
        var browserConfig = new BrowserConfiguration { PaywalledDomains = ["nytimes.com"] };
        var cookieManager = Substitute.For<ICookieManager>();

        // Start with no cookies
        cookieManager.GetCookieInfoAsync().Returns(new CookieInfo { Exists = false, IsExpired = false });

        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            browserConfig: browserConfig,
            cookieManager: cookieManager);

        // Simulate: paywalled domain was marked as needsJs (from skipping without cookies)
        var needsJsField = typeof(BackgroundPreloadService)
            .GetField("_needsJsDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var needsJsDict = (ConcurrentDictionary<string, bool>)needsJsField!.GetValue(service)!;
        needsJsDict["https://www.nytimes.com"] = true;

        // First refresh: no cookies, domain stays in _needsJsDomains
        var refreshMethod = typeof(BackgroundPreloadService)
            .GetMethod("RefreshPaywalledCookieState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        refreshMethod!.Invoke(service, null);
        needsJsDict.Should().ContainKey("https://www.nytimes.com");

        // Now cookies become available
        cookieManager.GetCookieInfoAsync().Returns(new CookieInfo { Exists = true, IsExpired = false });
        refreshMethod.Invoke(service, null);

        // Paywalled domain should be cleared from _needsJsDomains
        needsJsDict.Should().NotContainKey("https://www.nytimes.com",
            "paywalled domain should be cleared from needsJs when cookies become available");

        // Verify queue now includes the paywalled URL
        var nodes = CreateContentNodes("https://www.nytimes.com/2026/01/article.html");
        var queue = service.BuildQueue(0, nodes, "https://www.nytimes.com");
        queue.Should().HaveCount(1, "paywalled domain with cookies should be queued after needsJs cleared");
    }

    [Fact]
    public void RefreshPaywalledCookieState_CallsHttpCookieRefresher_WhenCookiesAppear()
    {
        var browserConfig = new BrowserConfiguration { PaywalledDomains = ["nytimes.com"] };
        var cookieManager = Substitute.For<ICookieManager>();
        var httpCookieRefresher = Substitute.For<IHttpCookieRefresher>();
        httpCookieRefresher.RefreshAsync().Returns(Task.CompletedTask);

        cookieManager.GetCookieInfoAsync().Returns(new CookieInfo { Exists = false, IsExpired = false });

        using var service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            NullLogger<BackgroundPreloadService>.Instance,
            browserConfig: browserConfig,
            cookieManager: cookieManager,
            httpCookieRefresher: httpCookieRefresher);

        var refreshMethod = typeof(BackgroundPreloadService)
            .GetMethod("RefreshPaywalledCookieState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // First refresh: no cookies — refresher should NOT be called
        refreshMethod.Invoke(service, null);
        httpCookieRefresher.DidNotReceive().RefreshAsync();

        // Cookies appear — refresher MUST be called
        cookieManager.GetCookieInfoAsync().Returns(new CookieInfo { Exists = true, IsExpired = false });
        refreshMethod.Invoke(service, null);
        httpCookieRefresher.Received(1).RefreshAsync();

        // Subsequent refresh with cookies still present — NOT called again (edge transition only)
        httpCookieRefresher.ClearReceivedCalls();
        refreshMethod.Invoke(service, null);
        httpCookieRefresher.DidNotReceive().RefreshAsync();
    }

    #endregion
}
