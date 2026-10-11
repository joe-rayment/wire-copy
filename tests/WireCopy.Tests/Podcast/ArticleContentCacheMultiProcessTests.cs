// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Two cache instances over one directory stand in for two processes (the app and the
/// browser-extension native host): each has its own in-memory index, and they only
/// share what is on disk.
/// </summary>
[Trait("Category", "Unit")]
public class ArticleContentCacheMultiProcessTests : IDisposable
{
    private const string UrlX = "https://example.com/article-x";
    private const string UrlY = "https://example.com/article-y";

    private readonly string _tempDir;

    public ArticleContentCacheMultiProcessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"article-cache-mp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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

    // Guards:   An instance that already loaded its index does not erase entries another
    //           process wrote to the same cache directory when it saves its own entry.
    // Fails if: PutAsync saves its stale in-memory index without re-reading index.json under the lock.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter TwoInstances_BothPuts_SurviveInFreshInstance
    [Fact]
    public async Task TwoInstances_BothPuts_SurviveInFreshInstance()
    {
        var a = NewCache();
        var b = NewCache();

        await a.TryGetAsync("https://example.com/anything");
        await b.PutAsync(UrlX, CreateArticle(UrlX, "text of x"));
        await a.PutAsync(UrlY, CreateArticle(UrlY, "text of y"));

        var fresh = NewCache();
        var x = await fresh.TryGetAsync(UrlX);
        var y = await fresh.TryGetAsync(UrlY);

        x.Should().NotBeNull("the entry written by the other instance must not be overwritten");
        x!.CleanedText.Should().Be("text of x");
        y.Should().NotBeNull();
        y!.CleanedText.Should().Be("text of y");
    }

    // Guards:   A running instance sees an entry another process added, without being recreated.
    // Fails if: TryGetAsync loads index.json once and never notices that the file changed.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter TryGet_SeesEntryPutByOtherInstance
    [Fact]
    public async Task TryGet_SeesEntryPutByOtherInstance()
    {
        var a = NewCache();
        var b = NewCache();

        (await a.TryGetAsync(UrlX)).Should().BeNull("nothing has been cached yet");

        await b.PutAsync(UrlX, CreateArticle(UrlX, "text of x"));

        var hit = await a.TryGetAsync(UrlX);
        hit.Should().NotBeNull("instance A must pick up the entry B wrote");
        hit!.CleanedText.Should().Be("text of x");
    }

    // Guards:   A running instance sees an entry another process removed.
    // Fails if: the instance keeps serving its in-memory copy after index.json dropped the entry.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter TryGet_SeesEntryRemovedByOtherInstance
    [Fact]
    public async Task TryGet_SeesEntryRemovedByOtherInstance()
    {
        var a = NewCache();
        var b = NewCache();

        await a.PutAsync(UrlX, CreateArticle(UrlX, "text of x"));
        (await a.TryGetAsync(UrlX)).Should().NotBeNull("A just cached it");

        await b.RemoveAsync(UrlX);

        (await a.TryGetAsync(UrlX)).Should().BeNull("B removed the entry, so A must not serve it any more");
    }

    private static ExtractedArticle CreateArticle(string url, string text) => new()
    {
        Title = "Title for " + url,
        CleanedText = text,
        Author = "Test Author",
        Url = url,
        WordCount = text.Split(' ').Length,
        PublishedDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
    };

    private ArticleContentCache NewCache() => new(
        _tempDir,
        TimeSpan.FromHours(1),
        100,
        NullLogger<ArticleContentCache>.Instance);
}
