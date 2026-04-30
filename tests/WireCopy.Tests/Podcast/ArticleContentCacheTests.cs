// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class ArticleContentCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ArticleContentCache _cache;

    public ArticleContentCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"article-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _cache = new ArticleContentCache(
            _tempDir,
            TimeSpan.FromHours(1),
            100,
            NullLogger<ArticleContentCache>.Instance);
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

    private static ExtractedArticle CreateArticle(
        string title = "Test Article",
        string url = "https://example.com/article",
        string text = "Some article text content here.")
    {
        return new ExtractedArticle
        {
            Title = title,
            CleanedText = text,
            Author = "Test Author",
            Url = url,
            WordCount = text.Split(' ').Length,
            PublishedDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    #region TryGetAsync

    [Fact]
    public async Task TryGetAsync_CacheMiss_ReturnsNull()
    {
        var result = await _cache.TryGetAsync("https://example.com/article");
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_AfterPut_ReturnsCachedArticle()
    {
        var article = CreateArticle();
        await _cache.PutAsync("https://example.com/article", article);

        var result = await _cache.TryGetAsync("https://example.com/article");

        result.Should().NotBeNull();
        result!.Title.Should().Be("Test Article");
        result.CleanedText.Should().Be("Some article text content here.");
        result.Author.Should().Be("Test Author");
        result.Url.Should().Be("https://example.com/article");
        result.WordCount.Should().Be(5);
        result.PublishedDate.Should().Be(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task TryGetAsync_NormalizesUrl_StripsTrackingParams()
    {
        var article = CreateArticle();
        await _cache.PutAsync("https://example.com/article?utm_source=twitter", article);

        var result = await _cache.TryGetAsync("https://example.com/article");

        result.Should().NotBeNull("normalized URL should match regardless of tracking params");
    }

    [Fact]
    public async Task TryGetAsync_NormalizesUrl_CaseInsensitiveHost()
    {
        var article = CreateArticle();
        await _cache.PutAsync("https://Example.COM/article", article);

        var result = await _cache.TryGetAsync("https://example.com/article");

        result.Should().NotBeNull("host should be case-insensitive");
    }

    [Fact]
    public async Task TryGetAsync_DifferentUrls_ReturnNull()
    {
        var article = CreateArticle();
        await _cache.PutAsync("https://example.com/article1", article);

        var result = await _cache.TryGetAsync("https://example.com/article2");

        result.Should().BeNull();
    }

    #endregion

    #region TTL Expiry

    [Fact]
    public async Task TryGetAsync_ExpiredEntry_ReturnsNull()
    {
        var shortTtlCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.Zero,
            100,
            NullLogger<ArticleContentCache>.Instance);

        var article = CreateArticle();
        await shortTtlCache.PutAsync("https://example.com/article", article);

        var result = await shortTtlCache.TryGetAsync("https://example.com/article");

        result.Should().BeNull("entry should be expired with zero TTL");
    }

    #endregion

    #region PutAsync

    [Fact]
    public async Task PutAsync_Overwrites_ExistingEntry()
    {
        var url = "https://example.com/article";

        await _cache.PutAsync(url, CreateArticle(title: "Version 1"));
        await _cache.PutAsync(url, CreateArticle(title: "Version 2"));

        var result = await _cache.TryGetAsync(url);
        result.Should().NotBeNull();
        result!.Title.Should().Be("Version 2");
    }

    [Fact]
    public async Task PutAsync_EvictsOldest_WhenOverMaxEntries()
    {
        var smallCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.FromHours(1),
            3,
            NullLogger<ArticleContentCache>.Instance);

        await smallCache.PutAsync("https://example.com/1", CreateArticle(title: "Article 1"));
        await smallCache.PutAsync("https://example.com/2", CreateArticle(title: "Article 2"));
        await smallCache.PutAsync("https://example.com/3", CreateArticle(title: "Article 3"));
        // This should evict Article 1 (oldest)
        await smallCache.PutAsync("https://example.com/4", CreateArticle(title: "Article 4"));

        var evicted = await smallCache.TryGetAsync("https://example.com/1");
        var kept = await smallCache.TryGetAsync("https://example.com/4");

        evicted.Should().BeNull("oldest entry should be evicted");
        kept.Should().NotBeNull("newest entry should be kept");
    }

    [Fact]
    public async Task PutAsync_EvictsExpired_BeforeCountEviction()
    {
        // Use a cache with TTL so short that entries expire immediately
        var shortTtlCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.Zero,
            100,
            NullLogger<ArticleContentCache>.Instance);

        // Put an entry that will immediately expire
        await shortTtlCache.PutAsync("https://example.com/old", CreateArticle(title: "Old"));

        // Put a new entry — the expired "old" entry should be evicted during this Put
        await shortTtlCache.PutAsync("https://example.com/new", CreateArticle(title: "New"));

        // Verify by loading from a new instance that reads the persisted index
        var freshCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.FromHours(1),
            100,
            NullLogger<ArticleContentCache>.Instance);

        var oldResult = await freshCache.TryGetAsync("https://example.com/old");
        var newResult = await freshCache.TryGetAsync("https://example.com/new");

        oldResult.Should().BeNull("expired entry should have been evicted during Put");
        newResult.Should().NotBeNull("newly put entry should survive");
    }

    #endregion

    #region Persistence

    [Fact]
    public async Task Index_PersistsAcrossInstances()
    {
        var article = CreateArticle();
        await _cache.PutAsync("https://example.com/article", article);

        // Create a new cache instance with the same base path
        var newCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.FromHours(1),
            100,
            NullLogger<ArticleContentCache>.Instance);

        var result = await newCache.TryGetAsync("https://example.com/article");

        result.Should().NotBeNull("index should be loaded from disk");
        result!.Title.Should().Be("Test Article");
        result.CleanedText.Should().Be("Some article text content here.");
    }

    [Fact]
    public async Task Index_CreatesDirectoryIfMissing()
    {
        var newDir = Path.Combine(_tempDir, "nested", "dir");
        var cache = new ArticleContentCache(newDir, TimeSpan.FromHours(1), 100, NullLogger<ArticleContentCache>.Instance);

        await cache.PutAsync("https://example.com/article", CreateArticle());

        Directory.Exists(newDir).Should().BeTrue();
        File.Exists(Path.Combine(newDir, "index.json")).Should().BeTrue();
    }

    #endregion

    #region Null Author and PublishedDate

    [Fact]
    public async Task RoundTrips_NullableFields()
    {
        var article = new ExtractedArticle
        {
            Title = "No Author",
            CleanedText = "Content without author info.",
            Url = "https://example.com/no-author",
            WordCount = 4,
            Author = null,
            PublishedDate = null,
        };

        await _cache.PutAsync("https://example.com/no-author", article);

        var result = await _cache.TryGetAsync("https://example.com/no-author");

        result.Should().NotBeNull();
        result!.Author.Should().BeNull();
        result.PublishedDate.Should().BeNull();
    }

    #endregion

    #region File-Based Storage

    [Fact]
    public async Task PutAsync_StoresTextInSeparateFile()
    {
        await _cache.PutAsync("https://example.com/article", CreateArticle());

        var articlesDir = Path.Combine(_tempDir, "articles");
        Directory.Exists(articlesDir).Should().BeTrue("articles subdirectory should be created");

        var txtFiles = Directory.GetFiles(articlesDir, "*.txt");
        txtFiles.Should().HaveCount(1, "one article text file should exist");

        var storedText = await File.ReadAllTextAsync(txtFiles[0]);
        storedText.Should().Be("Some article text content here.");
    }

    [Fact]
    public async Task PutAsync_IndexDoesNotContainCleanedText()
    {
        await _cache.PutAsync("https://example.com/article", CreateArticle());

        var indexJson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "index.json"));

        // The index should contain articleFilePath but not the full article text
        indexJson.Should().Contain("articleFilePath");
        indexJson.Should().NotContain("Some article text content here.");
    }

    [Fact]
    public async Task PutAsync_Overwrite_DeletesOldArticleFile()
    {
        var url = "https://example.com/article";

        await _cache.PutAsync(url, CreateArticle(text: "Version 1 text"));

        var articlesDir = Path.Combine(_tempDir, "articles");
        var filesAfterFirstPut = Directory.GetFiles(articlesDir, "*.txt");
        filesAfterFirstPut.Should().HaveCount(1);

        // Overwrite same URL — same hash, so same file path (overwritten in place)
        await _cache.PutAsync(url, CreateArticle(text: "Version 2 text"));

        var filesAfterSecondPut = Directory.GetFiles(articlesDir, "*.txt");
        filesAfterSecondPut.Should().HaveCount(1, "overwriting same URL should not leave orphan files");

        var storedText = await File.ReadAllTextAsync(filesAfterSecondPut[0]);
        storedText.Should().Be("Version 2 text");
    }

    [Fact]
    public async Task PutAsync_EvictsOldest_DeletesArticleFiles()
    {
        var smallCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.FromHours(1),
            2,
            NullLogger<ArticleContentCache>.Instance);

        await smallCache.PutAsync("https://example.com/1", CreateArticle(title: "Article 1"));
        await smallCache.PutAsync("https://example.com/2", CreateArticle(title: "Article 2"));

        var articlesDir = Path.Combine(_tempDir, "articles");
        Directory.GetFiles(articlesDir, "*.txt").Should().HaveCount(2);

        // This should evict Article 1 and delete its file
        await smallCache.PutAsync("https://example.com/3", CreateArticle(title: "Article 3"));

        Directory.GetFiles(articlesDir, "*.txt").Should().HaveCount(2, "evicted entry's file should be deleted");
    }

    [Fact]
    public async Task PutAsync_EvictsExpired_DeletesArticleFiles()
    {
        var shortTtlCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.Zero,
            100,
            NullLogger<ArticleContentCache>.Instance);

        await shortTtlCache.PutAsync("https://example.com/old", CreateArticle(title: "Old"));

        var articlesDir = Path.Combine(_tempDir, "articles");
        Directory.GetFiles(articlesDir, "*.txt").Should().HaveCount(1);

        // The "old" entry expires immediately; putting a new one should evict it
        await shortTtlCache.PutAsync("https://example.com/new", CreateArticle(title: "New"));

        Directory.GetFiles(articlesDir, "*.txt").Should().HaveCount(1, "expired entry's file should be deleted");
    }

    #endregion

    #region Self-Healing

    [Fact]
    public async Task TryGetAsync_MissingArticleFile_ReturnsNullAndRemovesEntry()
    {
        await _cache.PutAsync("https://example.com/article", CreateArticle());

        // Delete the article file to simulate corruption
        var articlesDir = Path.Combine(_tempDir, "articles");
        foreach (var file in Directory.GetFiles(articlesDir, "*.txt"))
        {
            File.Delete(file);
        }

        var result = await _cache.TryGetAsync("https://example.com/article");

        result.Should().BeNull("entry should be removed when article file is missing");

        // Verify the index was cleaned up by loading a fresh instance
        var freshCache = new ArticleContentCache(
            _tempDir,
            TimeSpan.FromHours(1),
            100,
            NullLogger<ArticleContentCache>.Instance);

        var freshResult = await freshCache.TryGetAsync("https://example.com/article");
        freshResult.Should().BeNull("entry should have been removed from persisted index");
    }

    #endregion

    #region Backward Compatibility

    [Fact]
    public async Task TryGetAsync_OldFormatWithInlineText_ServesContent()
    {
        // Simulate old-format index.json with inline CleanedText and no ArticleFilePath
        var oldFormatEntries = new[]
        {
            new
            {
                normalizedUrl = UrlNormalizer.Normalize("https://example.com/legacy"),
                title = "Legacy Article",
                cleanedText = "This is legacy inline content.",
                author = "Old Author",
                url = "https://example.com/legacy",
                wordCount = 5,
                publishedDate = (DateTime?)new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                cachedAtUtc = DateTime.UtcNow,
            },
        };

        var json = JsonSerializer.Serialize(oldFormatEntries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "index.json"), json);

        var cache = new ArticleContentCache(
            _tempDir,
            TimeSpan.FromHours(1),
            100,
            NullLogger<ArticleContentCache>.Instance);

        var result = await cache.TryGetAsync("https://example.com/legacy");

        result.Should().NotBeNull("old-format entries with inline text should still be served");
        result!.Title.Should().Be("Legacy Article");
        result.CleanedText.Should().Be("This is legacy inline content.");
        result.Author.Should().Be("Old Author");
    }

    #endregion
}
