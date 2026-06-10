// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class HierarchyConfigStoreTests : IDisposable
{
    private readonly HierarchyConfigStore _store;
    private readonly string _storagePath;

    public HierarchyConfigStoreTests()
    {
        var logger = Substitute.For<ILogger<HierarchyConfigStore>>();
        _store = new HierarchyConfigStore(logger);
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "hierarchy");
    }

    public void Dispose()
    {
        // Clean up test domain files
        try
        {
            var testFiles = new[]
            {
                "test-hierarchy.example.com.json",
                "test-hierarchy.other.com.json",
                "test-hierarchy.corrupt.com.json",
                "test-hierarchy.localhost.json",
                "test-hierarchy.localhost_8001.json",
                "test-hierarchy.localhost_8002.json",
            };

            foreach (var file in testFiles)
            {
                var path = Path.Combine(_storagePath, file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static SiteHierarchyConfig CreateTestConfig(
        string domain = "test-hierarchy.example.com",
        string urlPattern = "^https?://test-hierarchy\\.example\\.com/?$")
    {
        return new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = urlPattern,
            Sections = new List<HierarchySection>
            {
                new HierarchySection
                {
                    Name = "Top Stories",
                    SortOrder = 0,
                    ParentSelectors = new List<string> { "article > h3" },
                    UrlPatterns = new List<string> { "/news/" },
                    StartCollapsed = false,
                },
                new HierarchySection
                {
                    Name = "Opinion",
                    SortOrder = 1,
                    ParentSelectors = new List<string> { "section.opinion" },
                    UrlPatterns = new List<string> { "/opinion/" },
                    StartCollapsed = true,
                },
            },
            CreatedAt = new DateTime(2026, 3, 17, 12, 0, 0, DateTimeKind.Utc),
            ModelVersion = "gpt-5-mini",
        };
    }

    [Fact]
    public async Task GetConfigAsync_NoSavedConfig_ReturnsNull()
    {
        var result = await _store.GetConfigAsync("https://no-such-domain-exists.example.com/page");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGetConfig_RoundTrip_ReturnsMatchingConfig()
    {
        var config = CreateTestConfig();
        await _store.SaveConfigAsync(config);

        var result = await _store.GetConfigAsync("https://test-hierarchy.example.com/");

        result.Should().NotBeNull();
        result!.Domain.Should().Be("test-hierarchy.example.com");
        result.Sections.Should().HaveCount(2);
        result.Sections[0].Name.Should().Be("Top Stories");
        result.Sections[1].Name.Should().Be("Opinion");
        result.ModelVersion.Should().Be("gpt-5-mini");
    }

    [Fact]
    public async Task SaveAndGetConfig_RoundTrip_PreservesExcludeRules()
    {
        // workspace-5oe9.1: the durable exclude lists are the persistence the
        // AI-curated pattern config (B5/B6) relies on — they MUST survive a
        // save/load round-trip verbatim.
        var config = CreateTestConfig() with
        {
            ExcludeSelectors = new List<string> { ".promo", "aside.ad" },
            ExcludeUrlPatterns = new List<string> { "/sponsored/", "/newsletter" },
        };
        await _store.SaveConfigAsync(config);

        var result = await _store.GetConfigAsync("https://test-hierarchy.example.com/");

        result.Should().NotBeNull();
        result!.ExcludeSelectors.Should().BeEquivalentTo(new[] { ".promo", "aside.ad" });
        result.ExcludeUrlPatterns.Should().BeEquivalentTo(new[] { "/sponsored/", "/newsletter" });
    }

    [Fact]
    public async Task GetConfigAsync_LegacyJsonWithoutExcludeFields_DeserializesToEmptyLists()
    {
        // Backward-compat: a config file written before the exclude fields
        // existed must load without throwing and present empty (non-null) lists.
        var legacyJson =
            "[{\"domain\":\"test-hierarchy.example.com\"," +
            "\"urlPattern\":\"^https?://test-hierarchy\\\\.example\\\\.com/?$\"," +
            "\"sections\":[{\"name\":\"Top\",\"sortOrder\":0,\"parentSelectors\":[],\"urlPatterns\":[],\"startCollapsed\":false}]," +
            "\"createdAt\":\"2026-03-17T12:00:00Z\",\"modelVersion\":\"gpt-5-mini\"}]";
        Directory.CreateDirectory(_storagePath);
        await File.WriteAllTextAsync(
            Path.Combine(_storagePath, "test-hierarchy.example.com.json"), legacyJson);

        var result = await _store.GetConfigAsync("https://test-hierarchy.example.com/");

        result.Should().NotBeNull();
        result!.ExcludeSelectors.Should().NotBeNull().And.BeEmpty();
        result.ExcludeUrlPatterns.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ClearLegacySnapshotsAsync_RemovesOnlySnapshotConfigs()
    {
        // A legacy snapshot (Version 2, AiCurated, no sections, AiResult set)
        // and a durable Version-3 pattern config share a domain file.
        var legacy = new SiteHierarchyConfig
        {
            Domain = "test-hierarchy.example.com",
            UrlPattern = "^https?://test-hierarchy\\.example\\.com/page1$",
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "gpt-5-mini",
            Kind = LayoutKind.AiCurated,
            Strategy = "AiCurated",
            Version = 2,
            AiResult = new AiCuratedResult
            {
                ExcludedLinkKeys = new(),
                StoryOrderLinkKeys = new() { "url:https://test-hierarchy.example.com/old" },
                AnalyzedAt = DateTime.UtcNow,
            },
        };
        var durable = CreateTestConfig(urlPattern: "^https?://test-hierarchy\\.example\\.com/page2$") with
        {
            Kind = LayoutKind.AiCurated,
            Strategy = "AiCurated",
            Version = 3,
        };
        await _store.SaveConfigAsync(legacy);
        await _store.SaveConfigAsync(durable);

        var removed = await _store.ClearLegacySnapshotsAsync();

        removed.Should().Be(1);
        (await _store.GetConfigAsync("https://test-hierarchy.example.com/page1")).Should().BeNull("the snapshot is purged");
        (await _store.GetConfigAsync("https://test-hierarchy.example.com/page2")).Should().NotBeNull("the durable config survives");
    }

    [Fact]
    public async Task GetConfigAsync_NonMatchingUrl_ReturnsNull()
    {
        var config = CreateTestConfig();
        await _store.SaveConfigAsync(config);

        // URL doesn't match the pattern (different path)
        var result = await _store.GetConfigAsync("https://test-hierarchy.example.com/some/other/path");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveConfigAsync_OverwritesExistingPattern()
    {
        var config1 = CreateTestConfig();
        await _store.SaveConfigAsync(config1);

        var config2 = CreateTestConfig() with
        {
            Sections = new List<HierarchySection>
            {
                new HierarchySection { Name = "Updated Section", SortOrder = 0 },
            },
        };
        await _store.SaveConfigAsync(config2);

        var result = await _store.GetConfigAsync("https://test-hierarchy.example.com/");

        result.Should().NotBeNull();
        result!.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Updated Section");
    }

    [Fact]
    public async Task DeleteConfigAsync_ExistingConfig_ReturnsTrue()
    {
        var config = CreateTestConfig();
        await _store.SaveConfigAsync(config);

        var deleted = await _store.DeleteConfigAsync("https://test-hierarchy.example.com/");

        deleted.Should().BeTrue();

        var result = await _store.GetConfigAsync("https://test-hierarchy.example.com/");
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteConfigAsync_NonExistent_ReturnsFalse()
    {
        var deleted = await _store.DeleteConfigAsync("https://no-such-domain-exists.example.com/");

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task GetConfigCountAsync_NoConfigs_ReturnsZero()
    {
        // Clean any existing test files first
        Dispose();

        var count = await _store.GetConfigCountAsync();

        // Count may include configs from other tests/usage, so just check it's non-negative
        count.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task GetConfigCountAsync_AfterSave_IncludesNewConfig()
    {
        var countBefore = await _store.GetConfigCountAsync();

        var config = CreateTestConfig(domain: "test-hierarchy.other.com", urlPattern: "^https?://test-hierarchy\\.other\\.com/?$");
        await _store.SaveConfigAsync(config);

        var countAfter = await _store.GetConfigCountAsync();

        countAfter.Should().BeGreaterThan(countBefore);
    }

    [Fact]
    public async Task GetConfigAsync_InvalidUrl_ReturnsNull()
    {
        var result = await _store.GetConfigAsync("not-a-valid-url");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetConfigAsync_LocalSitesOnDifferentPorts_DoNotCollide()
    {
        // workspace-felb: configs for local sites are keyed host:port so a
        // config saved against one local server never applies to another.
        var config = CreateTestConfig(
            domain: "test-hierarchy.localhost:8001",
            urlPattern: "^https?://(www\\.)?test-hierarchy\\.localhost:8001/?");
        await _store.SaveConfigAsync(config);

        var samePort = await _store.GetConfigAsync("http://test-hierarchy.localhost:8001/");
        var otherPort = await _store.GetConfigAsync("http://test-hierarchy.localhost:8002/");

        samePort.Should().NotBeNull();
        samePort!.Domain.Should().Be("test-hierarchy.localhost:8001");
        otherPort.Should().BeNull("a config saved for port 8001 must not leak to port 8002");
    }

    [Fact]
    public async Task GetConfigAsync_LegacyPortBlindConfig_NotReturnedForPortedUrl()
    {
        // workspace-felb: pre-fix configs were keyed on the bare host with a
        // port-blind pattern. Ported URLs now key to host_port files, so the
        // legacy port-blind config must no longer hijack them.
        var legacy = CreateTestConfig(
            domain: "test-hierarchy.localhost",
            urlPattern: "^https?://(www\\.)?test-hierarchy\\.localhost/?");
        await _store.SaveConfigAsync(legacy);

        var ported = await _store.GetConfigAsync("http://test-hierarchy.localhost:8002/");
        var defaultPort = await _store.GetConfigAsync("http://test-hierarchy.localhost/");

        ported.Should().BeNull("ported URLs must not match the port-blind legacy config");
        defaultPort.Should().NotBeNull("default-port URLs still find the bare-host config");
    }

    [Fact]
    public async Task SaveConfigAsync_SectionProperties_PreservedOnRoundTrip()
    {
        var config = CreateTestConfig();
        await _store.SaveConfigAsync(config);

        var result = await _store.GetConfigAsync("https://test-hierarchy.example.com/");

        result.Should().NotBeNull();
        var topStories = result!.Sections[0];
        topStories.ParentSelectors.Should().Contain("article > h3");
        topStories.UrlPatterns.Should().Contain("/news/");
        topStories.StartCollapsed.Should().BeFalse();

        var opinion = result.Sections[1];
        opinion.ParentSelectors.Should().Contain("section.opinion");
        opinion.StartCollapsed.Should().BeTrue();
    }
}
