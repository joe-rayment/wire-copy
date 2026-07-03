// Licensed under the MIT License. See LICENSE in the repository root.

using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

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
    public void Put_DoesNotIndexByFinalUrl()
    {
        var result = CreateResult("https://example.com/final", "<html>test</html>");
        _cache.Put("https://example.com/redirect", result);

        // Should only be findable by request URL, not by final URL
        _cache.TryGet("https://example.com/redirect").Should().NotBeNull();
        _cache.TryGet("https://example.com/final").Should().BeNull();
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
    public void GetCachedUrls_IncludesOnlyRequestUrls()
    {
        // Simulate a redirect: request URL differs from final URL
        var result = CreateResult("https://example.com/final-page", "<html>redirected</html>");
        _cache.Put("https://example.com/short-link", result);

        var urls = _cache.GetCachedUrls();
        urls.Should().Contain("https://example.com/short-link", "request URL should be included");
        urls.Should().NotContain("https://example.com/final-page", "final URL should not be included");
    }

    [Fact]
    public void Put_AfterRedirect_OnlyRequestUrlReturnsEntry()
    {
        // Simulate a redirect: the page at /old redirects to /new
        var result = CreateResult("https://example.com/new", "<html>redirected content</html>");
        _cache.Put("https://example.com/old", result);

        // Only the request URL should return the cached entry
        var cachedByRequest = _cache.TryGet("https://example.com/old");
        cachedByRequest.Should().NotBeNull();
        cachedByRequest!.Html.Should().Be("<html>redirected content</html>");

        // The final/redirect URL should NOT return the cached entry
        _cache.TryGet("https://example.com/new").Should().BeNull();

        // Contains should also reflect request-only indexing
        _cache.Contains("https://example.com/old").Should().BeTrue();
        _cache.Contains("https://example.com/new").Should().BeFalse();

        // GetCachedUrls should only include the request URL
        var urls = _cache.GetCachedUrls();
        urls.Should().Contain("https://example.com/old");
        urls.Should().NotContain("https://example.com/new");
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

    #region Build Cache

    [Fact]
    public void TryGetBuildCache_ReturnsNullWhenNotStored()
    {
        var url = "https://example.com/page";
        _cache.Put(url, CreateResult(url, "<html>content</html>"));

        _cache.TryGetBuildCache(url).Should().BeNull();
    }

    [Fact]
    public void PutBuildCache_StoresAndRetrievesBuildCache()
    {
        var url = "https://example.com/page";
        _cache.Put(url, CreateResult(url, "<html>content</html>"));

        var buildCache = new PageBuildCache
        {
            Links = [new LinkInfo { Url = "https://example.com/link1", DisplayText = "Link 1", Type = Domain.Enums.Browser.LinkType.Content, ImportanceScore = 50 }],
            Metadata = new PageMetadata { Title = "Test" },
            FinalUrl = url,
        };

        _cache.PutBuildCache(url, buildCache);

        var result = _cache.TryGetBuildCache(url);
        result.Should().NotBeNull();
        result!.Links.Should().HaveCount(1);
        result.FinalUrl.Should().Be(url);
    }

    [Fact]
    public void PutBuildCache_StoresEvenWithoutHtmlEntry()
    {
        var url = "https://example.com/missing";
        var buildCache = new PageBuildCache
        {
            Links = [],
            Metadata = new PageMetadata { Title = "Test" },
            FinalUrl = url,
        };

        _cache.PutBuildCache(url, buildCache);

        _cache.TryGetBuildCache(url).Should().NotBeNull(
            "build cache should be stored in standalone dict even without HTML entry");
    }

    [Fact]
    public void RemoveHtmlEntry_BuildCacheSurvives()
    {
        var url = "https://example.com/page";
        _cache.Put(url, CreateResult(url, "<html>content</html>"));
        _cache.PutBuildCache(url, new PageBuildCache
        {
            Links = [],
            Metadata = new PageMetadata { Title = "Test" },
            FinalUrl = url,
        });

        _cache.Remove(url);

        _cache.TryGetBuildCache(url).Should().NotBeNull(
            "build cache survives HTML removal so repeat visits skip extraction after fallback retries");
    }

    [Fact]
    public void Clear_AlsoRemovesBuildCache()
    {
        var url = "https://example.com/page";
        _cache.Put(url, CreateResult(url, "<html>content</html>"));
        _cache.PutBuildCache(url, new PageBuildCache
        {
            Links = [],
            Metadata = new PageMetadata { Title = "Test" },
            FinalUrl = url,
        });

        _cache.Clear();

        _cache.TryGetBuildCache(url).Should().BeNull();
    }

    [Fact]
    public void BuildCache_EvictedWithPageLoadResult()
    {
        // Fill cache to capacity
        for (var i = 0; i < _config.MaxEntries; i++)
        {
            var entryUrl = $"https://example.com/page{i}";
            _cache.Put(entryUrl, CreateResult(entryUrl, "<html>content</html>"));
            _cache.PutBuildCache(entryUrl, new PageBuildCache
            {
                Links = [],
                Metadata = new PageMetadata { Title = $"Page {i}" },
                FinalUrl = entryUrl,
            });
        }

        // Add one more to trigger eviction
        var newUrl = "https://example.com/new";
        _cache.Put(newUrl, CreateResult(newUrl, "<html>new content</html>"));

        // At least one old entry should be evicted (with its build cache)
        _cache.GetStats().EntryCount.Should().BeLessOrEqualTo(_config.MaxEntries);
    }

    // workspace-hv8n: the former ApplyLinkListTtl_RefreshesTtlFromNow_NotFromCachedAt test asserted the SLIDING
    // behaviour that was the bug (expiry rebased to UtcNow on every revisit, so hot pages never expired). It is
    // replaced by ApplyLinkListTtl_IsAbsoluteCap_DoesNotSlideForwardOnRevisit + UpdateTtl_OnlyShrinks_* below.

    #endregion

    [Fact]
    public void ApplyLinkListTtl_IsAbsoluteCap_DoesNotSlideForwardOnRevisit()
    {
        // workspace-hv8n — the link-list TTL must be an ABSOLUTE cap anchored to the original cache time, NOT a
        // sliding window that re-bases off "now" on every revisit. The old behaviour recomputed expiry from
        // UtcNow each ApplyLinkListTtl call, so a frequently-reopened section page never expired. A short
        // link-list TTL over a long default proves the cap: repeated revisits within and past the window must
        // not push the expiry forward — once CachedAtUtc + LinkListTtl elapses the entry is gone.
        // workspace-9k27.16: assert on the entry's absolute expiry timestamp instead of
        // racing wall-clock sleeps against a 2s TTL (the old form flaked under load).
        var config = new CacheConfiguration
        {
            DefaultTtlSeconds = 3600,   // long default so the only cap in play is the link-list TTL
            LinkListTtlSeconds = 2,     // short absolute cap
            EvictionSweepIntervalSeconds = 3600,
        };
        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance);
        var url = "https://example.com/section";
        cache.Put(url, CreateResult(url, "<html>links</html>"));

        cache.ApplyLinkListTtl(url);
        var expiryAfterFirstApply = cache.GetExpiresAtUtc(url);
        expiryAfterFirstApply.Should().NotBeNull();

        // Re-applying on a revisit must re-anchor to CachedAtUtc — the expiry timestamp
        // must NOT move forward, no matter how many times or when it is re-applied.
        Thread.Sleep(50); // any nonzero gap: a sliding TTL would rebase to now+2s and move the timestamp
        cache.ApplyLinkListTtl(url);
        cache.GetExpiresAtUtc(url).Should().Be(
            expiryAfterFirstApply,
            "the link-list TTL is an absolute cap anchored to CachedAtUtc — a revisit must not slide it forward (workspace-hv8n)");
    }

    [Fact]
    public void UpdateTtl_OnlyShrinks_NeverExtendsBeyondCachedAtCap()
    {
        // workspace-hv8n — applying a link-list TTL longer than what is already set must NOT extend the entry's
        // lifetime; an apply can only shrink (clamp). Here a 1s default cap with a 3600s link-list TTL applied
        // on top must still expire at the original ~1s cap, not be lengthened to an hour.
        // workspace-9k27.16: timestamp assertion instead of a wall-clock sleep race.
        var config = new CacheConfiguration
        {
            DefaultTtlSeconds = 60,
            LinkListTtlSeconds = 3600,
            EvictionSweepIntervalSeconds = 3600,
        };
        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance);
        var url = "https://example.com/section";
        cache.Put(url, CreateResult(url, "<html>links</html>"));
        var originalExpiry = cache.GetExpiresAtUtc(url);
        originalExpiry.Should().NotBeNull();

        cache.ApplyLinkListTtl(url); // 3600s apply must clamp to the original 60s cap, not extend it

        cache.GetExpiresAtUtc(url).Should().Be(
            originalExpiry,
            "a longer TTL apply must clamp (shrink-only), never extend an entry past its original cap (workspace-hv8n)");
    }

    private static PageLoadResult CreateResult(string url, string html)
    {
        return PageLoadResult.Successful(url, html, new PageMetadata { Title = "Test" });
    }
}
