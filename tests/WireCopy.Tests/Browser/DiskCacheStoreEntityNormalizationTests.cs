// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression coverage for workspace-5zta: cache entries that were written
/// before the upstream text normalization landed (or by code paths that
/// bypassed it) may contain raw HTML entities like "&amp;nbsp;". The
/// LoadAllBuildCaches read path now runs every visible text field through
/// TextNormalizer.NormalizeDisplayText so the link list never renders an
/// unencoded entity, regardless of when the cache entry was produced.
/// </summary>
[Trait("Category", "Unit")]
public class DiskCacheStoreEntityNormalizationTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly DiskCacheStore _store;

    public DiskCacheStoreEntityNormalizationTests()
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

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LoadAllBuildCaches_StripsHtmlEntitiesFromDisplayText()
    {
        var rawLink = new LinkInfo
        {
            Url = "https://example.com/a",
            DisplayText = "Foo&nbsp;Bar&amp;Baz",
            Type = LinkType.Content,
            ImportanceScore = 50,
        };

        var buildCache = new PageBuildCache
        {
            Links = new() { rawLink },
            Metadata = new PageMetadata { Title = "T" },
            FinalUrl = "https://example.com/a",
        };

        _store.WriteBuildCache("https://example.com/a", buildCache);

        var loaded = _store.LoadAllBuildCaches();
        loaded.Should().ContainKey("https://example.com/a");

        var link = loaded["https://example.com/a"].Links[0];
        link.DisplayText.Should().Be("Foo Bar&Baz");
        link.DisplayText.Should().NotContain("&nbsp;");
        link.DisplayText.Should().NotContain("&amp;");
    }

    [Fact]
    public void LoadAllBuildCaches_StripsHtmlEntitiesFromAuthor()
    {
        var rawLink = new LinkInfo
        {
            Url = "https://example.com/a",
            DisplayText = "Article",
            Author = "Jane&nbsp;Doe",
            Type = LinkType.Content,
            ImportanceScore = 50,
        };

        var buildCache = new PageBuildCache
        {
            Links = new() { rawLink },
            Metadata = new PageMetadata { Title = "T" },
            FinalUrl = "https://example.com/a",
        };

        _store.WriteBuildCache("https://example.com/a", buildCache);

        var loaded = _store.LoadAllBuildCaches();
        var link = loaded["https://example.com/a"].Links[0];
        link.Author.Should().Be("Jane Doe");
    }

    [Fact]
    public void LoadAllBuildCaches_PreservesNullAuthor()
    {
        // The normalizer's null-passthrough wrapper must not convert null → "" —
        // downstream code branches on null vs. empty (e.g. fallback chains).
        var rawLink = new LinkInfo
        {
            Url = "https://example.com/a",
            DisplayText = "Article",
            Author = null,
            Type = LinkType.Content,
            ImportanceScore = 50,
        };

        var buildCache = new PageBuildCache
        {
            Links = new() { rawLink },
            Metadata = new PageMetadata { Title = "T" },
            FinalUrl = "https://example.com/a",
        };

        _store.WriteBuildCache("https://example.com/a", buildCache);

        var loaded = _store.LoadAllBuildCaches();
        var link = loaded["https://example.com/a"].Links[0];
        link.Author.Should().BeNull();
    }
}
