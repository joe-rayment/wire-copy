// Educational and personal use only.

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class BackgroundPreloadServiceTests
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

    #region BuildQueue - Top-to-Bottom Ordering

    [Fact]
    public void BuildQueue_SortedByListIndex()
    {
        var urls = Enumerable.Range(1, 5).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        var queue = _service.BuildQueue(3, nodes, "https://example.com");

        // All items should be sorted by their original list index (top-to-bottom)
        for (var i = 1; i < queue.Count; i++)
        {
            queue[i].ListIndex.Should().BeGreaterThan(queue[i - 1].ListIndex);
        }
    }

    [Fact]
    public void BuildQueue_PreservesOriginalListIndex()
    {
        var nodes = CreateContentNodes("https://example.com/a1", "https://example.com/a2", "https://example.com/a3");

        var queue = _service.BuildQueue(1, nodes, "https://example.com");

        queue.Should().HaveCount(3);
        queue[0].ListIndex.Should().Be(0);
        queue[1].ListIndex.Should().Be(1);
        queue[2].ListIndex.Should().Be(2);
    }

    [Fact]
    public void BuildQueue_SelectedIndexDoesNotAffectOrdering()
    {
        var urls = Enumerable.Range(1, 5).Select(i => $"https://example.com/a{i}").ToArray();
        var nodes = CreateContentNodes(urls);

        // Regardless of which item is selected, ordering is always top-to-bottom
        var queueStart = _service.BuildQueue(0, nodes, "https://example.com");
        var queueEnd = _service.BuildQueue(4, nodes, "https://example.com");

        queueStart.Select(i => i.Url).Should().Equal(queueEnd.Select(i => i.Url));
    }

    [Fact]
    public void BuildQueue_MixedNodeTypes_ListIndexReflectsOriginalPosition()
    {
        var root = LinkNode.CreateRoot();
        var groupHeader = LinkInfo.CreateGroupHeader(LinkType.Content);
        var article1 = new LinkInfo { Url = "https://example.com/a1", DisplayText = "A1", Type = LinkType.Content, ImportanceScore = 50 };
        var article2 = new LinkInfo { Url = "https://example.com/a2", DisplayText = "A2", Type = LinkType.Content, ImportanceScore = 50 };
        root.AddChild(groupHeader);  // index 0
        root.AddChild(article1);     // index 1
        root.AddChild(groupHeader);  // index 2
        root.AddChild(article2);     // index 3
        var nodes = root.Children.ToList();

        var queue = _service.BuildQueue(0, nodes, "https://example.com");

        queue.Should().HaveCount(2);
        queue[0].ListIndex.Should().Be(1, "article1 is at original index 1");
        queue[1].ListIndex.Should().Be(3, "article2 is at original index 3");
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
        var service = new BackgroundPreloadService(
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
    }

    [Fact]
    public void BuildQueue_CircuitBrokenDomain_RecoverAfterCooldown()
    {
        var cache = Substitute.For<IPageCache>();
        var config = new CacheConfiguration { CircuitBreakerCooldownSeconds = 1 };
        var service = new BackgroundPreloadService(
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
        var service = new BackgroundPreloadService(
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
        var service = new BackgroundPreloadService(
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
}
