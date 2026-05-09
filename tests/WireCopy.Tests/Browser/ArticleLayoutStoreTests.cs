// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ArticleLayoutStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ArticleLayoutStore _store;

    public ArticleLayoutStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wc-layouts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var logger = Substitute.For<ILogger<ArticleLayoutStore>>();
        _store = new ArticleLayoutStore(logger, _tempDir);
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
            // Best-effort cleanup
        }
    }

    private static ArticleSelectorConfig MakeConfig(string domain = "example.com")
    {
        return new ArticleSelectorConfig
        {
            Domain = domain,
            UpdatedAt = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc),
            PageTypes = new List<PageTypeEntry>
            {
                new()
                {
                    Name = "article",
                    PageType = PageType.Article,
                    Priority = 10,
                    Matcher = new PageTypeMatcher
                    {
                        UrlPattern = @"/\d{4}/\d{2}/\d{2}/",
                    },
                    Selectors = new ArticleSelectors
                    {
                        Headline = new List<string> { "//h1" },
                        Body = new List<string> { "//article" },
                    },
                    Quality = new ArticleQualityThresholds { MinWords = 100, MinParagraphs = 3 },
                    Provenance = new ProvenanceInfo
                    {
                        Model = "gpt-test",
                        SampleUrl = "https://example.com/2026/05/09/test",
                        ConsecutiveFailures = 0,
                        GeneratedAt = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc),
                    },
                },
            },
        };
    }

    [Fact]
    public async Task LoadAsync_NoSavedConfig_ReturnsNull()
    {
        var result = await _store.LoadAsync("missing-domain.example.com");

        result.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_EmptyDomain_ReturnsNull()
    {
        var result = await _store.LoadAsync(string.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_ReturnsEquivalentConfig()
    {
        var config = MakeConfig();
        await _store.SaveAsync(config);

        var loaded = await _store.LoadAsync(config.Domain);

        loaded.Should().NotBeNull();
        loaded!.Domain.Should().Be("example.com");
        loaded.PageTypes.Should().HaveCount(1);
        var entry = loaded.PageTypes[0];
        entry.Name.Should().Be("article");
        entry.PageType.Should().Be(PageType.Article);
        entry.Priority.Should().Be(10);
        entry.Matcher.UrlPattern.Should().Be(@"/\d{4}/\d{2}/\d{2}/");
        entry.Selectors.Headline.Should().ContainSingle().Which.Should().Be("//h1");
        entry.Selectors.Body.Should().ContainSingle().Which.Should().Be("//article");
        entry.Quality.MinWords.Should().Be(100);
        entry.Provenance.Model.Should().Be("gpt-test");
    }

    [Fact]
    public async Task SaveAsync_AtomicWrite_NoTempFileLeftBehind()
    {
        var config = MakeConfig();
        await _store.SaveAsync(config);

        var files = Directory.GetFiles(_tempDir);
        files.Should().HaveCount(1);
        files[0].Should().EndWith("example.com.json");
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty(
            "the atomic write should rename the temp file, not leave it on disk");
    }

    [Fact]
    public async Task SaveAsync_TwiceForSameDomain_OverwritesPriorContent()
    {
        var first = MakeConfig();
        await _store.SaveAsync(first);

        var second = first with
        {
            UpdatedAt = first.UpdatedAt.AddDays(1),
            PageTypes = new List<PageTypeEntry>
            {
                first.PageTypes[0] with { Priority = 100 },
            },
        };
        await _store.SaveAsync(second);

        var loaded = await _store.LoadAsync(first.Domain);
        loaded.Should().NotBeNull();
        loaded!.PageTypes[0].Priority.Should().Be(100);
        loaded.UpdatedAt.Should().Be(second.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_NullConfig_Throws()
    {
        var act = async () => await _store.SaveAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAsync_EmptyDomain_Throws()
    {
        var bad = MakeConfig() with { Domain = string.Empty };

        var act = async () => await _store.SaveAsync(bad);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task LoadAsync_SchemaVersionMismatch_ReturnsNullLeavingFileIntact()
    {
        var path = Path.Combine(_tempDir, "future.example.com.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 99,
              "domain": "future.example.com",
              "pageTypes": []
            }
            """);

        var loaded = await _store.LoadAsync("future.example.com");

        loaded.Should().BeNull("schema-version mismatch should be treated as a cache miss");
        File.Exists(path).Should().BeTrue("the file must not be clobbered on schema mismatch");
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "corrupt.example.com.json");
        File.WriteAllText(path, "{ this is not valid json");

        var loaded = await _store.LoadAsync("corrupt.example.com");

        loaded.Should().BeNull();
    }
}
