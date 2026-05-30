// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class DiskCacheStoreTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly DiskCacheStore _store;

    public DiskCacheStoreTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "wirecopy-test-cache-" + Guid.NewGuid().ToString("N"));
        _store = new DiskCacheStore(_cacheDir, NullLogger<InMemoryPageCache>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Write_CreatesFileOnDisk()
    {
        var (result, metadata) = CreateEntry("https://example.com/page", "<html>test</html>");

        _store.Write("https://example.com/page", result, metadata);

        var filePath = _store.GetFilePath("https://example.com/page");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public void Write_ThenLoadAll_RoundTrips()
    {
        var (result, metadata) = CreateEntry("https://example.com/page", "<html>round-trip</html>");

        _store.Write(metadata.NormalizedUrl, result, metadata);

        var loaded = _store.LoadAll();
        loaded.Should().HaveCount(1);
        loaded[0].Result.Url.Should().Be("https://example.com/page");
        loaded[0].Result.Html.Should().Be("<html>round-trip</html>");
        loaded[0].Result.Success.Should().BeTrue();
        loaded[0].Result.StatusCode.Should().Be(200);
        loaded[0].Metadata.RequestUrl.Should().Be("https://example.com/page");
        loaded[0].Metadata.FinalUrl.Should().Be("https://example.com/page");
    }

    [Fact]
    public void LoadAll_PreservesPageMetadata()
    {
        var pageMetadata = new PageMetadata
        {
            Title = "Test Title",
            Description = "Test description",
            Author = "Test Author",
            CanonicalUrl = "https://example.com/canonical",
            PublishedDate = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
            FaviconUrl = "https://example.com/favicon.ico",
        };

        var result = new PageLoadResult
        {
            Success = true,
            Url = "https://example.com/page",
            Html = "<html>metadata test</html>",
            Metadata = pageMetadata,
            StatusCode = 200,
            FetchMethod = FetchMethod.Http,
        };

        var metadata = new CacheEntryMetadata
        {
            RequestUrl = "https://example.com/page",
            FinalUrl = "https://example.com/page",
            NormalizedUrl = "https://example.com/page",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            SizeBytes = 2048,
        };

        _store.Write(metadata.NormalizedUrl, result, metadata);

        var loaded = _store.LoadAll();
        loaded.Should().HaveCount(1);
        var loadedMetadata = loaded[0].Result.Metadata;
        loadedMetadata.Should().NotBeNull();
        loadedMetadata!.Title.Should().Be("Test Title");
        loadedMetadata.Description.Should().Be("Test description");
        loadedMetadata.Author.Should().Be("Test Author");
        loadedMetadata.CanonicalUrl.Should().Be("https://example.com/canonical");
        loadedMetadata.PublishedDate.Should().Be(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        loadedMetadata.FaviconUrl.Should().Be("https://example.com/favicon.ico");
    }

    [Fact]
    public void LoadAll_SkipsExpiredEntries()
    {
        var result = PageLoadResult.Successful(
            "https://example.com/expired",
            "<html>expired</html>",
            new PageMetadata { Title = "Expired" });

        var metadata = new CacheEntryMetadata
        {
            RequestUrl = "https://example.com/expired",
            FinalUrl = "https://example.com/expired",
            NormalizedUrl = "https://example.com/expired",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-1), // Already expired
            SizeBytes = 1024,
        };

        _store.Write(metadata.NormalizedUrl, result, metadata);

        var loaded = _store.LoadAll();
        loaded.Should().BeEmpty();

        // Expired file should have been deleted
        var filePath = _store.GetFilePath(metadata.NormalizedUrl);
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public void LoadAll_SkipsCorruptFiles()
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllText(Path.Combine(_cacheDir, "corrupt.json"), "not valid json{{{");

        var loaded = _store.LoadAll();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_ReturnsEmptyWhenDirectoryDoesNotExist()
    {
        var store = new DiskCacheStore(
            Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N")),
            NullLogger<InMemoryPageCache>.Instance);

        store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void Delete_RemovesFileFromDisk()
    {
        var (result, metadata) = CreateEntry("https://example.com/to-delete", "<html>delete me</html>");
        _store.Write(metadata.NormalizedUrl, result, metadata);

        var filePath = _store.GetFilePath(metadata.NormalizedUrl);
        File.Exists(filePath).Should().BeTrue();

        _store.Delete(metadata.NormalizedUrl);
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public void Delete_NoopWhenFileDoesNotExist()
    {
        // Should not throw
        _store.Delete("https://example.com/nonexistent");
    }

    [Fact]
    public void ClearAll_RemovesAllFiles()
    {
        _store.Write("url1", CreateEntry("https://example.com/1", "a").Result, CreateEntry("https://example.com/1", "a").Metadata);
        _store.Write("url2", CreateEntry("https://example.com/2", "b").Result, CreateEntry("https://example.com/2", "b").Metadata);

        Directory.EnumerateFiles(_cacheDir, "*.json").Should().HaveCount(2);

        _store.ClearAll();

        Directory.EnumerateFiles(_cacheDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public void ClearAll_NoopWhenDirectoryDoesNotExist()
    {
        var store = new DiskCacheStore(
            Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N")),
            NullLogger<InMemoryPageCache>.Instance);

        // Should not throw
        store.ClearAll();
    }

    [Fact]
    public void WriteBuildCache_WithExcludeRules_RoundTripsThroughDisk()
    {
        // workspace-5oe9.1: the config-level exclude lists live as top-level
        // fields on the persisted build cache (NOT per-section) and must
        // survive a build-cache rehydrate.
        var url = "https://example.com/homepage";
        var buildCache = new PageBuildCache
        {
            Links = new List<LinkInfo>
            {
                new LinkInfo { Url = url + "/story", DisplayText = "Story", Type = LinkType.Content, ImportanceScore = 80 },
            },
            Metadata = new PageMetadata { Title = "Home" },
            FinalUrl = url,
            Classification = PageClassification.LinkList,
            ClassificationVersion = 1,
            CachedAt = DateTime.UtcNow,
            HierarchyConfig = new SiteHierarchyConfig
            {
                Domain = "example.com",
                UrlPattern = "^https?://example\\.com/homepage$",
                Sections = new List<HierarchySection>
                {
                    new HierarchySection { Name = "Lead", SortOrder = 0, ParentSelectors = new List<string> { "main .lead" } },
                },
                CreatedAt = DateTime.UtcNow,
                ModelVersion = "gpt-5-mini",
                ExcludeSelectors = new List<string> { ".promo", "aside.ad" },
                ExcludeUrlPatterns = new List<string> { "/sponsored/" },
            },
        };

        _store.WriteBuildCache(url, buildCache);

        var loaded = _store.LoadAllBuildCaches();
        var key = UrlNormalizer.Normalize(url);
        loaded.Should().ContainKey(key);
        loaded[key].HierarchyConfig.Should().NotBeNull();
        loaded[key].HierarchyConfig!.ExcludeSelectors.Should().BeEquivalentTo(new[] { ".promo", "aside.ad" });
        loaded[key].HierarchyConfig!.ExcludeUrlPatterns.Should().BeEquivalentTo(new[] { "/sponsored/" });
    }

    [Fact]
    public void WriteBuildCache_PersistsKindStrategyVersion()
    {
        // workspace-5oe9.6: a rehydrated config must be honest about its origin.
        var url = "https://news.example.com/home";
        var buildCache = new PageBuildCache
        {
            Links = new List<LinkInfo> { new() { Url = url + "/s", DisplayText = "S", Type = LinkType.Content, ImportanceScore = 80 } },
            Metadata = new PageMetadata { Title = "Home" },
            FinalUrl = url,
            Classification = PageClassification.LinkList,
            CachedAt = DateTime.UtcNow,
            HierarchyConfig = new SiteHierarchyConfig
            {
                Domain = "news.example.com",
                UrlPattern = "^https?://news\\.example\\.com/home$",
                Sections = new List<HierarchySection> { new() { Name = "Lead", SortOrder = 0, ParentSelectors = new List<string> { "section.lead" } } },
                CreatedAt = DateTime.UtcNow,
                ModelVersion = "gpt-5-mini",
                Kind = LayoutKind.AiCurated,
                Strategy = "AiCurated",
                Version = 3,
            },
        };

        _store.WriteBuildCache(url, buildCache);

        var loaded = _store.LoadAllBuildCaches();
        var cfg = loaded[UrlNormalizer.Normalize(url)].HierarchyConfig;
        cfg.Should().NotBeNull();
        cfg!.Kind.Should().Be(LayoutKind.AiCurated, "Kind must not default to AiHierarchical on rehydrate");
        cfg.Strategy.Should().Be("AiCurated");
        cfg.Version.Should().Be(3);
    }

    [Fact]
    public void Write_MultipleEntries_LoadAll_ReturnsAll()
    {
        for (int i = 0; i < 5; i++)
        {
            var url = $"https://example.com/page-{i}";
            var (result, metadata) = CreateEntry(url, $"<html>page {i}</html>");
            _store.Write(metadata.NormalizedUrl, result, metadata);
        }

        var loaded = _store.LoadAll();
        loaded.Should().HaveCount(5);
    }

    [Fact]
    public void InMemoryPageCache_LoadsFromDiskOnStartup()
    {
        // Write entries to disk
        var (result, metadata) = CreateEntry("https://example.com/persisted", "<html>persisted</html>");
        _store.Write(metadata.NormalizedUrl, result, metadata);

        // Create a new InMemoryPageCache with the same disk store
        var config = new CacheConfiguration
        {
            DiskCacheEnabled = true,
            EvictionSweepIntervalSeconds = 3600,
        };

        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance,
            _store);

        // Entry should be available from memory (loaded from disk)
        var cached = cache.TryGet("https://example.com/persisted");
        cached.Should().NotBeNull();
        cached!.Html.Should().Be("<html>persisted</html>");
    }

    [Fact]
    public void InMemoryPageCache_PersistsToDiskOnPut()
    {
        var config = new CacheConfiguration
        {
            DiskCacheEnabled = true,
            EvictionSweepIntervalSeconds = 3600,
        };

        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance,
            _store);

        var result = PageLoadResult.Successful(
            "https://example.com/new-page",
            "<html>new content</html>",
            new PageMetadata { Title = "New" });

        cache.Put("https://example.com/new-page", result);

        // Give the background write a moment to complete
        Thread.Sleep(200);

        // Verify it's on disk
        var loaded = _store.LoadAll();
        loaded.Should().HaveCount(1);
        loaded[0].Result.Html.Should().Be("<html>new content</html>");
    }

    [Fact]
    public void InMemoryPageCache_DeletesFromDiskOnRemove()
    {
        var (result, metadata) = CreateEntry("https://example.com/removable", "<html>removable</html>");
        _store.Write(metadata.NormalizedUrl, result, metadata);

        var config = new CacheConfiguration
        {
            DiskCacheEnabled = true,
            EvictionSweepIntervalSeconds = 3600,
        };

        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance,
            _store);

        cache.Remove("https://example.com/removable");

        var loaded = _store.LoadAll();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public void InMemoryPageCache_ClearsCacheAndDisk()
    {
        var (result, metadata) = CreateEntry("https://example.com/clearable", "<html>clearable</html>");
        _store.Write(metadata.NormalizedUrl, result, metadata);

        var config = new CacheConfiguration
        {
            DiskCacheEnabled = true,
            EvictionSweepIntervalSeconds = 3600,
        };

        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance,
            _store);

        cache.Clear();

        var loaded = _store.LoadAll();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public void InMemoryPageCache_WorksWithoutDiskStore()
    {
        var config = new CacheConfiguration
        {
            DiskCacheEnabled = false,
            EvictionSweepIntervalSeconds = 3600,
        };

        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance,
            diskStore: null);

        var result = PageLoadResult.Successful(
            "https://example.com/memory-only",
            "<html>memory only</html>",
            new PageMetadata { Title = "Memory" });

        cache.Put("https://example.com/memory-only", result);

        cache.TryGet("https://example.com/memory-only").Should().NotBeNull();
    }

    [Fact]
    public void EnforceSizeLimit_EvictsOldestFilesWhenOverLimit()
    {
        // Create a store with a very small disk limit
        var dir = Path.Combine(Path.GetTempPath(), "wirecopy-test-sizelimit-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Write 5 entries, each ~500+ bytes on disk, with staggered mtime
            var store = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance, maxDiskSizeBytes: long.MaxValue);
            for (int i = 0; i < 5; i++)
            {
                var url = $"https://example.com/size-{i}";
                var (result, metadata) = CreateEntry(url, $"<html>{'X' + new string('Y', 200)}</html>");
                store.Write(metadata.NormalizedUrl, result, metadata);

                // Stagger last write times so ordering is deterministic
                var filePath = store.GetFilePath(metadata.NormalizedUrl);
                File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-50 + (i * 10)));
            }

            // Measure total size of all 5 files
            var totalSize = new DirectoryInfo(dir).GetFiles("*.json").Sum(f => f.Length);
            totalSize.Should().BeGreaterThan(0);

            // Set a limit that fits ~2 files (40% of total)
            var twoFileLimit = (long)(totalSize * 0.4);
            var limitedStore = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance, maxDiskSizeBytes: twoFileLimit);
            limitedStore.EnforceSizeLimit();

            var remainingFiles = Directory.GetFiles(dir, "*.json");
            remainingFiles.Length.Should().BeLessThan(5, "oldest files should have been evicted");

            var remainingSize = remainingFiles.Sum(f => new FileInfo(f).Length);
            remainingSize.Should().BeLessOrEqualTo(twoFileLimit, "total disk size should be within limit");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void EnforceSizeLimit_NoopWhenUnderLimit()
    {
        // Write entries with the default (large) limit
        for (int i = 0; i < 3; i++)
        {
            var url = $"https://example.com/noop-{i}";
            var (result, metadata) = CreateEntry(url, $"<html>small entry {i}</html>");
            _store.Write(metadata.NormalizedUrl, result, metadata);
        }

        // All 3 files should still exist (default 500MB limit is not reached)
        Directory.GetFiles(_cacheDir, "*.json").Length.Should().Be(3);
    }

    [Fact]
    public void EnforceSizeLimit_NoopWhenLimitIsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wirecopy-test-nolimit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance, maxDiskSizeBytes: 0);
            for (int i = 0; i < 3; i++)
            {
                var url = $"https://example.com/nolimit-{i}";
                var (result, metadata) = CreateEntry(url, $"<html>entry {i}</html>");
                store.Write(metadata.NormalizedUrl, result, metadata);
            }

            // With limit=0 (disabled), no eviction should happen
            Directory.GetFiles(dir, "*.json").Length.Should().Be(3);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Write_EnforcesSizeLimitAfterWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wirecopy-test-writeevict-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Write 2 files without limit to measure their size
            var unlimitedStore = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance, maxDiskSizeBytes: long.MaxValue);
            var url1 = "https://example.com/evict-write-1";
            var (result1, meta1) = CreateEntry(url1, "<html>" + new string('A', 500) + "</html>");
            unlimitedStore.Write(meta1.NormalizedUrl, result1, meta1);
            File.SetLastWriteTimeUtc(unlimitedStore.GetFilePath(meta1.NormalizedUrl), DateTime.UtcNow.AddMinutes(-10));

            var singleFileSize = new FileInfo(unlimitedStore.GetFilePath(meta1.NormalizedUrl)).Length;

            // Now create a store with a limit that fits only ~1.5 files
            var limitedStore = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance, maxDiskSizeBytes: (long)(singleFileSize * 1.5));

            // Write a second entry; this should trigger eviction of the first (oldest)
            var url2 = "https://example.com/evict-write-2";
            var (result2, meta2) = CreateEntry(url2, "<html>" + new string('B', 500) + "</html>");
            limitedStore.Write(meta2.NormalizedUrl, result2, meta2);

            var remaining = Directory.GetFiles(dir, "*.json");
            remaining.Length.Should().Be(1, "only the newest file should remain after eviction");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void LoadAll_EnforcesSizeLimitAfterLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wirecopy-test-loadevict-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Write 5 entries without limit
            var unlimitedStore = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance, maxDiskSizeBytes: long.MaxValue);
            for (int i = 0; i < 5; i++)
            {
                var url = $"https://example.com/load-evict-{i}";
                var (result, metadata) = CreateEntry(url, $"<html>{new string('Z', 300)}</html>");
                unlimitedStore.Write(metadata.NormalizedUrl, result, metadata);
                File.SetLastWriteTimeUtc(unlimitedStore.GetFilePath(metadata.NormalizedUrl), DateTime.UtcNow.AddMinutes(-50 + (i * 10)));
            }

            var totalSize = new DirectoryInfo(dir).GetFiles("*.json").Sum(f => f.Length);
            var oneFileSize = totalSize / 5;

            // Create a limited store that fits ~2 files
            var limitedStore = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance, maxDiskSizeBytes: oneFileSize * 2 + 10);
            var loaded = limitedStore.LoadAll();

            // LoadAll should have triggered eviction
            var remaining = Directory.GetFiles(dir, "*.json");
            remaining.Length.Should().BeLessOrEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void MaxDiskSizeBytes_DefaultIs500MB()
    {
        var config = new CacheConfiguration();
        config.MaxDiskSizeBytes.Should().Be(500L * 1024 * 1024);
    }

    private static (PageLoadResult Result, CacheEntryMetadata Metadata) CreateEntry(string url, string html)
    {
        var result = PageLoadResult.Successful(url, html, new PageMetadata { Title = "Test" });

        var metadata = new CacheEntryMetadata
        {
            RequestUrl = url,
            FinalUrl = url,
            NormalizedUrl = UrlNormalizer.Normalize(url),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(html) + 1024,
        };

        return (result, metadata);
    }
}
