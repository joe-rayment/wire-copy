// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast.Cache;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class FileSystemTtsAudioCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemTtsAudioCache _cache;

    public FileSystemTtsAudioCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tts-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var cacheConfig = Options.Create(new TtsAudioCacheConfiguration
        {
            BasePath = _tempDir,
            MaxSizeBytes = 10 * 1024 * 1024, // 10 MB for tests
            Ttl = TimeSpan.FromHours(1),
        });

        var ttsConfig = Options.Create(new OpenAiTtsConfiguration
        {
            Voice = "nova",
            Model = "tts-1",
            Speed = 1.0f,
            OutputFormat = "aac",
        });

        var logger = Substitute.For<ILogger<FileSystemTtsAudioCache>>();
        _cache = new FileSystemTtsAudioCache(cacheConfig, ttsConfig, logger);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    #region TryGetAsync

    [Fact]
    public async Task TryGetAsync_CacheMiss_ReturnsNull()
    {
        var result = await _cache.TryGetAsync("some article text", "https://example.com/article");
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_AfterPut_ReturnsCachedEntry()
    {
        var text = "This is an article for caching.";
        var url = "https://example.com/article";
        var audioData = new byte[] { 1, 2, 3, 4, 5 };

        await _cache.PutAsync(text, url, "Test Article", audioData);

        var result = await _cache.TryGetAsync(text, url);

        result.Should().NotBeNull();
        result!.FileSizeBytes.Should().Be(5);
        result.ContentHash.Should().NotBeNullOrEmpty();
        result.TtsConfigHash.Should().NotBeNullOrEmpty();
        File.Exists(result.AudioFilePath).Should().BeTrue();
    }

    [Fact]
    public async Task TryGetAsync_SameTextDifferentUrl_ReturnsCachedEntry()
    {
        var text = "Same text, different URL.";
        var audioData = new byte[] { 10, 20, 30 };

        await _cache.PutAsync(text, "https://example.com/a", "Article A", audioData);

        var result = await _cache.TryGetAsync(text, "https://example.com/b");

        result.Should().NotBeNull("same text content should produce same cache key");
    }

    [Fact]
    public async Task TryGetAsync_DifferentText_ReturnsNull()
    {
        await _cache.PutAsync("text one", "https://example.com/1", "Article 1", new byte[] { 1 });

        var result = await _cache.TryGetAsync("text two", "https://example.com/1");

        result.Should().BeNull("different text should not match");
    }

    #endregion

    #region PutAsync

    [Fact]
    public async Task PutAsync_CreatesAudioFile()
    {
        var audioData = new byte[] { 0xFF, 0xFE, 0x01, 0x02 };
        var entry = await _cache.PutAsync("some text", "https://example.com", "Title", audioData);

        File.Exists(entry.AudioFilePath).Should().BeTrue();
        var fileContents = await File.ReadAllBytesAsync(entry.AudioFilePath);
        fileContents.Should().Equal(audioData);
    }

    [Fact]
    public async Task PutAsync_Overwrites_ExistingEntry()
    {
        var text = "article text";
        var url = "https://example.com";

        await _cache.PutAsync(text, url, "V1", new byte[] { 1, 2, 3 });
        var entry2 = await _cache.PutAsync(text, url, "V2", new byte[] { 4, 5, 6, 7 });

        entry2.FileSizeBytes.Should().Be(4);

        var retrieved = await _cache.TryGetAsync(text, url);
        retrieved.Should().NotBeNull();
        retrieved!.FileSizeBytes.Should().Be(4);
    }

    [Fact]
    public async Task PutAsync_ReturnsCorrectCacheKey()
    {
        var entry = await _cache.PutAsync("text", "https://example.com", "Title", new byte[] { 1 });

        entry.CacheKey.Should().NotBeNullOrEmpty();
        entry.CacheKey.Should().Contain("_", "key should contain content hash and config hash separated by underscore");
    }

    #endregion

    #region AnalyzeCollectionAsync

    [Fact]
    public async Task AnalyzeCollectionAsync_AllUncached_ReturnsCorrectCounts()
    {
        var articles = new List<(string Url, string Title, string Text)>
        {
            ("https://example.com/1", "Article 1", "Text of article one"),
            ("https://example.com/2", "Article 2", "Text of article two"),
        };

        var analysis = await _cache.AnalyzeCollectionAsync(articles);

        analysis.TotalArticles.Should().Be(2);
        analysis.CachedArticles.Should().Be(0);
        analysis.UncachedArticles.Should().Be(2);
        analysis.EstimatedCost.Should().BeGreaterThan(0);
        analysis.ArticleStatuses.Should().HaveCount(2);
        analysis.ArticleStatuses.All(s => !s.IsCached).Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeCollectionAsync_MixedCacheState_ReturnsCorrectCounts()
    {
        await _cache.PutAsync("Text of article one", "https://example.com/1", "Article 1", new byte[] { 1 });

        var articles = new List<(string Url, string Title, string Text)>
        {
            ("https://example.com/1", "Article 1", "Text of article one"),
            ("https://example.com/2", "Article 2", "Text of article two"),
        };

        var analysis = await _cache.AnalyzeCollectionAsync(articles);

        analysis.CachedArticles.Should().Be(1);
        analysis.UncachedArticles.Should().Be(1);
        analysis.ArticleStatuses[0].IsCached.Should().BeTrue();
        analysis.ArticleStatuses[0].EstimatedCost.Should().Be(0);
        analysis.ArticleStatuses[1].IsCached.Should().BeFalse();
        analysis.ArticleStatuses[1].EstimatedCost.Should().BeGreaterThan(0);
    }

    #endregion

    #region GetStatsAsync

    [Fact]
    public async Task GetStatsAsync_EmptyCache_ReturnsZeros()
    {
        var stats = await _cache.GetStatsAsync();

        stats.EntryCount.Should().Be(0);
        stats.TotalSizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task GetStatsAsync_WithEntries_ReturnsCorrectStats()
    {
        await _cache.PutAsync("text one", "https://example.com/1", "A1", new byte[] { 1, 2, 3 });
        await _cache.PutAsync("text two", "https://example.com/2", "A2", new byte[] { 4, 5 });

        var stats = await _cache.GetStatsAsync();

        stats.EntryCount.Should().Be(2);
        stats.TotalSizeBytes.Should().Be(5);
    }

    #endregion

    #region EvictAsync

    [Fact]
    public async Task EvictAsync_RemovesExpiredEntries()
    {
        // Create cache with very short TTL
        var cacheConfig = Options.Create(new TtsAudioCacheConfiguration
        {
            BasePath = _tempDir,
            Ttl = TimeSpan.Zero, // Everything is immediately expired
        });

        var ttsConfig = Options.Create(new OpenAiTtsConfiguration());
        var logger = Substitute.For<ILogger<FileSystemTtsAudioCache>>();
        var shortTtlCache = new FileSystemTtsAudioCache(cacheConfig, ttsConfig, logger);

        await shortTtlCache.PutAsync("text", "https://example.com", "Title", new byte[] { 1 });

        var statsBefore = await shortTtlCache.GetStatsAsync();
        statsBefore.EntryCount.Should().Be(1);

        await shortTtlCache.EvictAsync();

        var statsAfter = await shortTtlCache.GetStatsAsync();
        statsAfter.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task EvictAsync_LruEviction_RemovesOldestAccessed()
    {
        // Create cache with tiny size limit
        var cacheConfig = Options.Create(new TtsAudioCacheConfiguration
        {
            BasePath = _tempDir,
            MaxSizeBytes = 5, // Very small - forces LRU eviction
            Ttl = TimeSpan.FromHours(1),
        });

        var ttsConfig = Options.Create(new OpenAiTtsConfiguration());
        var logger = Substitute.For<ILogger<FileSystemTtsAudioCache>>();
        var smallCache = new FileSystemTtsAudioCache(cacheConfig, ttsConfig, logger);

        await smallCache.PutAsync("text one", "https://example.com/1", "A1", new byte[] { 1, 2, 3 });
        await smallCache.PutAsync("text two", "https://example.com/2", "A2", new byte[] { 4, 5, 6 });

        // Total is 6 bytes > 5 limit
        await smallCache.EvictAsync();

        var stats = await smallCache.GetStatsAsync();
        stats.TotalSizeBytes.Should().BeLessOrEqualTo(5);
    }

    #endregion

    #region ClearAsync

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        await _cache.PutAsync("text one", "https://example.com/1", "A1", new byte[] { 1 });
        await _cache.PutAsync("text two", "https://example.com/2", "A2", new byte[] { 2 });

        await _cache.ClearAsync();

        var stats = await _cache.GetStatsAsync();
        stats.EntryCount.Should().Be(0);
        stats.TotalSizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task ClearAsync_DeletesAudioFiles()
    {
        var entry = await _cache.PutAsync("text", "https://example.com", "Title", new byte[] { 1 });
        var filePath = entry.AudioFilePath;
        File.Exists(filePath).Should().BeTrue();

        await _cache.ClearAsync();

        File.Exists(filePath).Should().BeFalse();
    }

    #endregion

    #region Index persistence

    [Fact]
    public async Task Index_PersistsAcrossInstances()
    {
        var text = "persistent article text";
        await _cache.PutAsync(text, "https://example.com", "Title", new byte[] { 1, 2, 3 });

        // Create new instance with same base path AND the same TTS config —
        // the cache key depends on Voice/Model/Speed/OutputFormat, so a
        // bare-defaults OpenAiTtsConfiguration would miss the entry written
        // above (workspace-clsl moved the defaults to gpt-4o-mini-tts/coral).
        var cacheConfig = Options.Create(new TtsAudioCacheConfiguration { BasePath = _tempDir });
        var ttsConfig = Options.Create(new OpenAiTtsConfiguration
        {
            Voice = "nova",
            Model = "tts-1",
            Speed = 1.0f,
            OutputFormat = "aac",
        });
        var logger = Substitute.For<ILogger<FileSystemTtsAudioCache>>();
        var newCache = new FileSystemTtsAudioCache(cacheConfig, ttsConfig, logger);

        var result = await newCache.TryGetAsync(text, "https://example.com");

        result.Should().NotBeNull("index should be loaded from disk");
        result!.FileSizeBytes.Should().Be(3);
    }

    #endregion

    #region TTS config sensitivity

    [Fact]
    public async Task DifferentTtsConfig_SeparateCacheEntries()
    {
        var text = "same text different config";

        // Cache with default config (nova voice)
        await _cache.PutAsync(text, "https://example.com", "Title", new byte[] { 1 });

        // Create cache with different voice
        var cacheConfig = Options.Create(new TtsAudioCacheConfiguration { BasePath = _tempDir });
        var ttsConfig = Options.Create(new OpenAiTtsConfiguration { Voice = "alloy" });
        var logger = Substitute.For<ILogger<FileSystemTtsAudioCache>>();
        var differentVoiceCache = new FileSystemTtsAudioCache(cacheConfig, ttsConfig, logger);

        var result = await differentVoiceCache.TryGetAsync(text, "https://example.com");

        result.Should().BeNull("different TTS config should not match");
    }

    #endregion

    #region Corrupt cache resilience

    [Fact]
    public async Task AnalyzeCollectionAsync_CorruptIndexFile_ReturnsGracefulFallback()
    {
        // Put a valid entry first, so an index file exists
        await _cache.PutAsync("text one", "https://example.com/1", "A1", new byte[] { 1 });

        // Create a new cache instance with corrupt index
        var indexDir = Path.Combine(_tempDir, "index");
        if (Directory.Exists(indexDir))
        {
            foreach (var file in Directory.GetFiles(indexDir, "*.json"))
            {
                await File.WriteAllTextAsync(file, "NOT VALID JSON {{{{}}}}");
            }
        }
        else
        {
            // Index might be at cache root
            var indexFiles = Directory.GetFiles(_tempDir, "*.json");
            foreach (var file in indexFiles)
            {
                await File.WriteAllTextAsync(file, "NOT VALID JSON {{{{}}}}");
            }
        }

        var cacheConfig = Options.Create(new TtsAudioCacheConfiguration
        {
            BasePath = _tempDir,
            MaxSizeBytes = 10 * 1024 * 1024,
            Ttl = TimeSpan.FromHours(1),
        });
        var ttsConfig = Options.Create(new OpenAiTtsConfiguration
        {
            Voice = "nova",
            Model = "tts-1",
            Speed = 1.0f,
            OutputFormat = "aac",
        });
        var logger = Substitute.For<ILogger<FileSystemTtsAudioCache>>();
        var corruptCache = new FileSystemTtsAudioCache(cacheConfig, ttsConfig, logger);

        var articles = new List<(string Url, string Title, string Text)>
        {
            ("https://example.com/1", "Article 1", "Text of article one"),
        };

        // Should NOT throw — catch block returns graceful fallback
        var analysis = await corruptCache.AnalyzeCollectionAsync(articles);

        analysis.Should().NotBeNull();
        analysis.TotalArticles.Should().Be(1);
    }

    [Fact]
    public async Task TryGetAsync_CorruptIndexFile_ReturnsNull()
    {
        // Put a valid entry first
        await _cache.PutAsync("text", "https://example.com", "Title", new byte[] { 1 });

        // Corrupt the index
        var indexFiles = Directory.GetFiles(_tempDir, "*.json", SearchOption.AllDirectories);
        foreach (var file in indexFiles)
        {
            await File.WriteAllTextAsync(file, "CORRUPT {{{");
        }

        var cacheConfig = Options.Create(new TtsAudioCacheConfiguration
        {
            BasePath = _tempDir,
            Ttl = TimeSpan.FromHours(1),
        });
        var ttsConfig = Options.Create(new OpenAiTtsConfiguration { Voice = "nova", Model = "tts-1", Speed = 1.0f, OutputFormat = "aac" });
        var logger = Substitute.For<ILogger<FileSystemTtsAudioCache>>();
        var corruptCache = new FileSystemTtsAudioCache(cacheConfig, ttsConfig, logger);

        // Should NOT throw — catch block returns null
        var result = await corruptCache.TryGetAsync("text", "https://example.com");

        result.Should().BeNull("corrupt index should gracefully return null");
    }

    #endregion
}
