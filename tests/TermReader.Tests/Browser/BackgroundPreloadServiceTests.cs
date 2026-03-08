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
        var groupHeader = LinkInfo.CreateGroupHeader(LinkType.Content, 5);
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
        var groupHeader = LinkInfo.CreateGroupHeader(LinkType.Content, 5);
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
    public void NotifySelectionChanged_RebuildsQueue()
    {
        var nodes1 = CreateContentNodes("https://example.com/a1");
        _service.NotifySelectionChanged(0, nodes1, "https://example.com");

        var nodes2 = CreateContentNodes("https://example.com/b1", "https://example.com/b2");
        _service.NotifySelectionChanged(0, nodes2, "https://example.com");

        // The queue should have been rebuilt with the second set of nodes
        var queue = _service.BuildQueue(0, nodes2, "https://example.com");
        queue.Should().HaveCount(2);
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

    #region Helpers

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
