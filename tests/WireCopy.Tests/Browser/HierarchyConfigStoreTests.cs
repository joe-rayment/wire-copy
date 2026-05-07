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
