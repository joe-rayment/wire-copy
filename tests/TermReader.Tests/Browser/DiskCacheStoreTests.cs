// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class DiskCacheStoreTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly DiskCacheStore _store;

    public DiskCacheStoreTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "termreader-test-cache-" + Guid.NewGuid().ToString("N"));
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
