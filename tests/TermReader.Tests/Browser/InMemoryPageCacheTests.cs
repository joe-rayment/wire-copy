// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class InMemoryPageCacheTests : IDisposable
{
    private readonly InMemoryPageCache _cache;
    private readonly CacheConfiguration _config;

    public InMemoryPageCacheTests()
    {
        _config = new CacheConfiguration
        {
            MaxSizeBytes = 10 * 1024 * 1024, // 10 MB for tests
            MaxEntries = 10,
            DefaultTtlSeconds = 3600, // 1 hour
            MaxEntrySizeBytes = 5 * 1024 * 1024,
            EvictionSweepIntervalSeconds = 3600 // Disable sweep in tests
        };
        _cache = new InMemoryPageCache(
            Options.Create(_config),
            NullLogger<InMemoryPageCache>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenNotCached()
    {
        _cache.TryGet("https://example.com/page").Should().BeNull();
    }

    [Fact]
    public void Put_ThenTryGet_ReturnsCachedResult()
    {
        var result = CreateResult("https://example.com/page", "<html>test</html>");
        _cache.Put("https://example.com/page", result);

        var cached = _cache.TryGet("https://example.com/page");

        cached.Should().NotBeNull();
        cached!.Url.Should().Be("https://example.com/page");
        cached.Html.Should().Be("<html>test</html>");
    }

    [Fact]
    public void Put_NormalizesUrl()
    {
        var result = CreateResult("https://example.com/page", "<html>test</html>");
        _cache.Put("https://Example.COM/page/?utm_source=twitter", result);

        _cache.TryGet("https://example.com/page").Should().NotBeNull();
    }

    [Fact]
    public void Put_IndexesByFinalUrl()
    {
        var result = CreateResult("https://example.com/final", "<html>test</html>");
        _cache.Put("https://example.com/redirect", result);

        // Should be findable by both request URL and final URL
        _cache.TryGet("https://example.com/redirect").Should().NotBeNull();
        _cache.TryGet("https://example.com/final").Should().NotBeNull();
    }

    [Fact]
    public void Put_DoesNotCacheFailedResults()
    {
        var result = PageLoadResult.Failure("error");
        _cache.Put("https://example.com/page", result);

        _cache.TryGet("https://example.com/page").Should().BeNull();
    }

    [Fact]
    public void Put_SkipsOversizedEntries()
    {
        var config = new CacheConfiguration { MaxEntrySizeBytes = 100, EvictionSweepIntervalSeconds = 3600 };
        using var cache = new InMemoryPageCache(
            Options.Create(config), NullLogger<InMemoryPageCache>.Instance);

        var largeHtml = new string('x', 200);
        var result = CreateResult("https://example.com/big", largeHtml);
        cache.Put("https://example.com/big", result);

        cache.TryGet("https://example.com/big").Should().BeNull();
    }

    [Fact]
    public void TryGet_ReturnsNull_ForExpiredEntry()
    {
        var config = new CacheConfiguration { DefaultTtlSeconds = 0, EvictionSweepIntervalSeconds = 3600 };
        using var cache = new InMemoryPageCache(
            Options.Create(config), NullLogger<InMemoryPageCache>.Instance);

        var result = CreateResult("https://example.com/page", "<html>test</html>");
        cache.Put("https://example.com/page", result);

        // TTL is 0 seconds, so entry expires immediately
        cache.TryGet("https://example.com/page").Should().BeNull();
    }

    [Fact]
    public void Contains_ReturnsTrueForCachedUrl()
    {
        var result = CreateResult("https://example.com/page", "<html>test</html>");
        _cache.Put("https://example.com/page", result);

        _cache.Contains("https://example.com/page").Should().BeTrue();
    }

    [Fact]
    public void Contains_ReturnsFalseForUncachedUrl()
    {
        _cache.Contains("https://example.com/missing").Should().BeFalse();
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        var result = CreateResult("https://example.com/page", "<html>test</html>");
        _cache.Put("https://example.com/page", result);

        _cache.Remove("https://example.com/page").Should().BeTrue();
        _cache.TryGet("https://example.com/page").Should().BeNull();
    }

    [Fact]
    public void Remove_ReturnsFalseForMissingEntry()
    {
        _cache.Remove("https://example.com/missing").Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _cache.Put("https://example.com/1", CreateResult("https://example.com/1", "<html>1</html>"));
        _cache.Put("https://example.com/2", CreateResult("https://example.com/2", "<html>2</html>"));

        var stats = _cache.Clear();

        stats.EntryCount.Should().Be(2);
        _cache.TryGet("https://example.com/1").Should().BeNull();
        _cache.TryGet("https://example.com/2").Should().BeNull();
        _cache.GetStats().EntryCount.Should().Be(0);
    }

    [Fact]
    public void GetStats_TracksHitsAndMisses()
    {
        var result = CreateResult("https://example.com/page", "<html>test</html>");
        _cache.Put("https://example.com/page", result);

        _cache.TryGet("https://example.com/page"); // hit
        _cache.TryGet("https://example.com/page"); // hit
        _cache.TryGet("https://example.com/missing"); // miss

        var stats = _cache.GetStats();
        stats.HitCount.Should().Be(2);
        stats.MissCount.Should().Be(1);
        stats.HitRatePercent.Should().BeApproximately(66.7, 0.1);
    }

    [Fact]
    public void GetStats_TracksSizeBytes()
    {
        var result = CreateResult("https://example.com/page", "<html>test</html>");
        _cache.Put("https://example.com/page", result);

        var stats = _cache.GetStats();
        stats.TotalSizeBytes.Should().BeGreaterThan(0);
        stats.MaxSizeBytes.Should().Be(_config.MaxSizeBytes);
    }

    [Fact]
    public void GetCachedUrls_ReturnsNonExpiredUrls()
    {
        _cache.Put("https://example.com/1", CreateResult("https://example.com/1", "<html>1</html>"));
        _cache.Put("https://example.com/2", CreateResult("https://example.com/2", "<html>2</html>"));

        var urls = _cache.GetCachedUrls();
        urls.Should().Contain("https://example.com/1");
        urls.Should().Contain("https://example.com/2");
    }

    [Fact]
    public void GetCachedAt_ReturnsTimestampForCachedUrl()
    {
        var before = DateTime.UtcNow;
        _cache.Put("https://example.com/page", CreateResult("https://example.com/page", "<html>test</html>"));
        var after = DateTime.UtcNow;

        var cachedAt = _cache.GetCachedAt("https://example.com/page");
        cachedAt.Should().NotBeNull();
        cachedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void GetCachedAt_ReturnsNullForUncachedUrl()
    {
        _cache.GetCachedAt("https://example.com/missing").Should().BeNull();
    }

    [Fact]
    public void EvictsLruEntry_WhenMaxEntriesReached()
    {
        var config = new CacheConfiguration { MaxEntries = 3, EvictionSweepIntervalSeconds = 3600 };
        using var cache = new InMemoryPageCache(
            Options.Create(config), NullLogger<InMemoryPageCache>.Instance);

        cache.Put("https://example.com/1", CreateResult("https://example.com/1", "a"));
        cache.Put("https://example.com/2", CreateResult("https://example.com/2", "b"));
        cache.Put("https://example.com/3", CreateResult("https://example.com/3", "c"));

        // Access entry 1 to make it recently used
        cache.TryGet("https://example.com/1");

        // Adding entry 4 should evict entry 2 (least recently accessed)
        cache.Put("https://example.com/4", CreateResult("https://example.com/4", "d"));

        cache.TryGet("https://example.com/1").Should().NotBeNull("entry 1 was recently accessed");
        cache.TryGet("https://example.com/4").Should().NotBeNull("entry 4 was just added");
        // Entry 2 or 3 should have been evicted (whichever was LRU)
        var stats = cache.GetStats();
        stats.EntryCount.Should().BeLessOrEqualTo(3);
    }

    [Fact]
    public void EvictsLruEntries_WhenSizeLimitReached()
    {
        var config = new CacheConfiguration
        {
            MaxSizeBytes = 3072, // ~3KB
            MaxEntries = 100,
            MaxEntrySizeBytes = 5 * 1024 * 1024,
            EvictionSweepIntervalSeconds = 3600
        };
        using var cache = new InMemoryPageCache(
            Options.Create(config), NullLogger<InMemoryPageCache>.Instance);

        // Each entry is ~1KB (tiny HTML + 1024 overhead)
        cache.Put("https://example.com/1", CreateResult("https://example.com/1", "a"));
        cache.Put("https://example.com/2", CreateResult("https://example.com/2", "b"));
        cache.Put("https://example.com/3", CreateResult("https://example.com/3", "c"));

        // This should trigger eviction
        cache.Put("https://example.com/4", CreateResult("https://example.com/4", "d"));

        var stats = cache.GetStats();
        stats.TotalSizeBytes.Should().BeLessOrEqualTo(config.MaxSizeBytes + 1024);
    }

    [Fact]
    public void Put_UpdatesExistingEntry()
    {
        var result1 = CreateResult("https://example.com/page", "<html>version1</html>");
        var result2 = CreateResult("https://example.com/page", "<html>version2</html>");

        _cache.Put("https://example.com/page", result1);
        _cache.Put("https://example.com/page", result2);

        var cached = _cache.TryGet("https://example.com/page");
        cached.Should().NotBeNull();
        cached!.Html.Should().Be("<html>version2</html>");
    }

    [Fact]
    public void CacheStats_FormattedSize_FormatsBytes()
    {
        var stats = new CacheStats { TotalSizeBytes = 500, MaxSizeBytes = 1024 };
        stats.FormattedSize.Should().Be("500 B");
    }

    [Fact]
    public void CacheStats_FormattedSize_FormatsKilobytes()
    {
        var stats = new CacheStats { TotalSizeBytes = 2048, MaxSizeBytes = 1024 * 1024 };
        stats.FormattedSize.Should().Be("2.0 KB");
    }

    [Fact]
    public void CacheStats_FormattedSize_FormatsMegabytes()
    {
        var stats = new CacheStats { TotalSizeBytes = 50 * 1024 * 1024, MaxSizeBytes = 100 * 1024 * 1024 };
        stats.FormattedSize.Should().Be("50.0 MB");
    }

    [Fact]
    public void CacheStats_HitRatePercent_ZeroWhenNoRequests()
    {
        var stats = new CacheStats { HitCount = 0, MissCount = 0 };
        stats.HitRatePercent.Should().Be(0);
    }

    private static PageLoadResult CreateResult(string url, string html)
    {
        return PageLoadResult.Successful(url, html, new PageMetadata { Title = "Test" });
    }
}
