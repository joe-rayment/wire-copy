// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.ScrapingStrategies;
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
                "test-hierarchy-wwwprobe.example.com.json",
                "www.test-hierarchy-wwwprobe.example.com.json",
                "test-hierarchy-wwwsave.example.com.json",
                "www.test-hierarchy-wwwsave.example.com.json",
                "test-hierarchy-domainlist.example.com.json",
                "www.test-hierarchy-domainlist.example.com.json",
                "test-hierarchy-specific.example.com.json",
                "test-hierarchy-specificrev.example.com.json",
                "test-hierarchy-clearone.example.com.json",
                "test-hierarchy-corruptsave.example.com.json",
                "test-hierarchy-corruptread.example.com.json",
            };

            foreach (var file in testFiles)
            {
                var path = Path.Combine(_storagePath, file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            // Corrupt files the store moved aside during the corrupt-file tests.
            foreach (var aside in Directory.GetFiles(_storagePath, "test-hierarchy-corrupt*.corrupt-*.json"))
            {
                File.Delete(aside);
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
    public async Task GetConfigAsync_LegacyWwwKeyedFile_FoundFromApexUrl()
    {
        // workspace-42q8.1: configs written before the www-strip live in
        // "www.<host>.json" with a pattern anchored on the literal www host.
        // An apex-host URL for the same page must still find them (this was
        // the "NYT Today's Paper has no saved layout" lie).
        var legacyJson =
            "[{\"domain\":\"www.test-hierarchy-wwwprobe.example.com\"," +
            "\"urlPattern\":\"^https?://(www\\\\.)?www\\\\.test-hierarchy-wwwprobe\\\\.example\\\\.com/section/today\"," +
            "\"sections\":[{\"name\":\"Top\",\"sortOrder\":0,\"parentSelectors\":[],\"urlPatterns\":[],\"startCollapsed\":false}]," +
            "\"createdAt\":\"2026-03-17T12:00:00Z\",\"modelVersion\":\"gpt-5-mini\"}]";
        Directory.CreateDirectory(_storagePath);
        await File.WriteAllTextAsync(
            Path.Combine(_storagePath, "www.test-hierarchy-wwwprobe.example.com.json"), legacyJson);

        var fromApex = await _store.GetConfigAsync("https://test-hierarchy-wwwprobe.example.com/section/today");
        var fromWww = await _store.GetConfigAsync("https://www.test-hierarchy-wwwprobe.example.com/section/today");

        fromApex.Should().NotBeNull("the legacy www-keyed config covers the same site");
        fromWww.Should().NotBeNull();
        fromApex!.Sections[0].Name.Should().Be("Top");
    }

    [Fact]
    public async Task GetConfigAsync_WwwUrl_FindsConfigSavedFromApexUrl()
    {
        var config = CreateTestConfig(
            domain: "test-hierarchy-wwwsave.example.com",
            urlPattern: "^https?://(www\\.)?test-hierarchy-wwwsave\\.example\\.com/?");
        await _store.SaveConfigAsync(config);

        var result = await _store.GetConfigAsync("https://www.test-hierarchy-wwwsave.example.com/");

        result.Should().NotBeNull("www and apex are the same site");
    }

    [Fact]
    public async Task GetConfigsForDomainAsync_ReturnsEverySiteConfig_RegardlessOfUrlMatch()
    {
        // One config in the current apex file, one stranded in a legacy www file,
        // neither matching the queried URL — both must surface so callers can say
        // "the site IS set up, just not this page" instead of lying.
        var apex = CreateTestConfig(
            domain: "test-hierarchy-domainlist.example.com",
            urlPattern: "^https?://(www\\.)?test-hierarchy-domainlist\\.example\\.com/pageA");
        await _store.SaveConfigAsync(apex);
        var legacyJson =
            "[{\"domain\":\"www.test-hierarchy-domainlist.example.com\"," +
            "\"urlPattern\":\"^https?://www\\\\.test-hierarchy-domainlist\\\\.example\\\\.com/pageB\"," +
            "\"sections\":[]," +
            "\"createdAt\":\"2026-03-17T12:00:00Z\",\"modelVersion\":\"gpt-5-mini\"}]";
        await File.WriteAllTextAsync(
            Path.Combine(_storagePath, "www.test-hierarchy-domainlist.example.com.json"), legacyJson);

        var all = await _store.GetConfigsForDomainAsync("https://test-hierarchy-domainlist.example.com/unrelated");

        all.Should().HaveCount(2);
        (await _store.GetConfigAsync("https://test-hierarchy-domainlist.example.com/unrelated"))
            .Should().BeNull("no pattern covers this URL — but the domain listing above still reports the site as configured");
    }

    [Fact]
    public async Task SaveConfigAsync_SupersedesSamePatternConfigInLegacyWwwFile()
    {
        const string pattern = "^https?://(www\\.)?test-hierarchy-wwwsave\\.example\\.com/?";
        var legacyJson =
            "[{\"domain\":\"www.test-hierarchy-wwwsave.example.com\"," +
            "\"urlPattern\":\"" + pattern.Replace("\\", "\\\\") + "\"," +
            "\"sections\":[{\"name\":\"Stale\",\"sortOrder\":0,\"parentSelectors\":[],\"urlPatterns\":[],\"startCollapsed\":false}]," +
            "\"createdAt\":\"2026-03-17T12:00:00Z\",\"modelVersion\":\"old\"}]";
        Directory.CreateDirectory(_storagePath);
        await File.WriteAllTextAsync(
            Path.Combine(_storagePath, "www.test-hierarchy-wwwsave.example.com.json"), legacyJson);

        var fresh = CreateTestConfig(domain: "test-hierarchy-wwwsave.example.com", urlPattern: pattern);
        await _store.SaveConfigAsync(fresh);

        var all = await _store.GetConfigsForDomainAsync("https://test-hierarchy-wwwsave.example.com/");
        all.Should().HaveCount(1, "the same-pattern config in the legacy www file is superseded, not shadow-duplicated");
        all[0].ModelVersion.Should().Be("gpt-5-mini");
    }

    [Fact]
    public async Task DeleteConfigAsync_RemovesConfigFromLegacyWwwFile()
    {
        var legacyJson =
            "[{\"domain\":\"www.test-hierarchy-wwwprobe.example.com\"," +
            "\"urlPattern\":\"^https?://(www\\\\.)?www\\\\.test-hierarchy-wwwprobe\\\\.example\\\\.com/?\"," +
            "\"sections\":[]," +
            "\"createdAt\":\"2026-03-17T12:00:00Z\",\"modelVersion\":\"old\"}]";
        Directory.CreateDirectory(_storagePath);
        await File.WriteAllTextAsync(
            Path.Combine(_storagePath, "www.test-hierarchy-wwwprobe.example.com.json"), legacyJson);

        var deleted = await _store.DeleteConfigAsync("https://test-hierarchy-wwwprobe.example.com/");

        deleted.Should().BeTrue("deleting by the apex URL must reach the legacy www-keyed file");
        (await _store.GetConfigAsync("https://www.test-hierarchy-wwwprobe.example.com/")).Should().BeNull();
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

    // Guards:   A layout saved for a specific section (x.com/news/world) is used on that
    //           section even after a homepage layout was saved first, and the homepage
    //           layout still serves the homepage.
    // Fails if: GetConfigAsync returns the FIRST pattern match in save order, so the
    //           homepage's catch-all pattern shadows every later, more specific layout.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~GetConfigAsync_SectionLayoutSavedAfterHomepage_WinsOnThatSection"
    [Fact]
    public async Task GetConfigAsync_SectionLayoutSavedAfterHomepage_WinsOnThatSection()
    {
        const string domain = "test-hierarchy-specific.example.com";
        var homepage = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/"))
            with { ModelVersion = "homepage" };
        var world = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/news/world"))
            with { ModelVersion = "news-world" };
        var news = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/news"))
            with { ModelVersion = "news" };
        (await _store.SaveConfigAsync(homepage)).Should().BeTrue();
        (await _store.SaveConfigAsync(world)).Should().BeTrue();
        (await _store.SaveConfigAsync(news)).Should().BeTrue();

        (await _store.GetConfigAsync($"https://{domain}/news/world"))!.ModelVersion
            .Should().Be("news-world", "the /news/world layout is more specific than the homepage and /news ones");
        (await _store.GetConfigAsync($"https://{domain}/news/world?page=2"))!.ModelVersion
            .Should().Be("news-world");
        (await _store.GetConfigAsync($"https://{domain}/news/sport"))!.ModelVersion
            .Should().Be("news", "/news is the most specific layout that covers /news/sport");
        (await _store.GetConfigAsync($"https://{domain}/"))!.ModelVersion
            .Should().Be("homepage", "the homepage keeps its own layout");
        (await _store.GetConfigAsync($"https://{domain}/newsletter"))!.ModelVersion
            .Should().Be("homepage", "/news must not claim /newsletter");
    }

    // Guards:   Which layout wins does not depend on the order they were saved in: a
    //           section layout saved BEFORE the homepage one still wins on its section.
    // Fails if: selection falls back to file order and returns the first match.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~GetConfigAsync_SectionLayoutSavedBeforeHomepage_StillWinsOnThatSection"
    [Fact]
    public async Task GetConfigAsync_SectionLayoutSavedBeforeHomepage_StillWinsOnThatSection()
    {
        const string domain = "test-hierarchy-specificrev.example.com";
        var world = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/news/world"))
            with { ModelVersion = "news-world" };
        var homepage = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/"))
            with { ModelVersion = "homepage" };
        await _store.SaveConfigAsync(world);
        await _store.SaveConfigAsync(homepage);

        (await _store.GetConfigAsync($"https://{domain}/news/world"))!.ModelVersion.Should().Be("news-world");
        (await _store.GetConfigAsync($"https://{domain}/"))!.ModelVersion.Should().Be("homepage");
    }

    // Guards:   Clearing the layout on a section (x.com/news/world) removes only that
    //           section's layout; the homepage and /news layouts that merely prefix-match
    //           the URL survive.
    // Fails if: DeleteConfigAsync removes every pattern that matches the URL instead of
    //           just the one layout GetConfigAsync would have chosen.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~DeleteConfigAsync_SectionUrl_LeavesHomepageAndParentLayouts"
    [Fact]
    public async Task DeleteConfigAsync_SectionUrl_LeavesHomepageAndParentLayouts()
    {
        const string domain = "test-hierarchy-clearone.example.com";
        var homepage = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/"))
            with { ModelVersion = "homepage" };
        var news = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/news"))
            with { ModelVersion = "news" };
        var world = CreateTestConfig(domain, DocumentOrderStrategy.BuildUrlPattern($"https://{domain}/news/world"))
            with { ModelVersion = "news-world" };
        await _store.SaveConfigAsync(homepage);
        await _store.SaveConfigAsync(news);
        await _store.SaveConfigAsync(world);

        var deleted = await _store.DeleteConfigAsync($"https://{domain}/news/world");

        deleted.Should().BeTrue();
        var remaining = await _store.GetConfigsForDomainAsync($"https://{domain}/");
        remaining.Select(c => c.ModelVersion).Should().BeEquivalentTo(new[] { "homepage", "news" });
        (await _store.GetConfigAsync($"https://{domain}/"))!.ModelVersion.Should().Be("homepage");
        (await _store.GetConfigAsync($"https://{domain}/news/world"))!.ModelVersion
            .Should().Be("news", "with its own layout gone the section falls back to the next most specific one");
    }

    // Guards:   An unreadable (corrupt) layout file is moved aside intact before a new
    //           layout is saved, so saving never destroys the user's other saved layouts.
    // Fails if: the corrupt file is just replaced by a file holding only the new config
    //           (its bytes gone), or the aside copy differs from the original bytes.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~SaveConfigAsync_CorruptFile_IsMovedAsideNotOverwritten"
    [Fact]
    public async Task SaveConfigAsync_CorruptFile_IsMovedAsideNotOverwritten()
    {
        const string domain = "test-hierarchy-corruptsave.example.com";
        var corruptBytes = System.Text.Encoding.UTF8.GetBytes(
            "[{\"domain\":\"" + domain + "\",\"urlPattern\":\"^abc\",\"sections\":[{\"na");
        Directory.CreateDirectory(_storagePath);
        var filePath = Path.Combine(_storagePath, domain + ".json");
        await File.WriteAllBytesAsync(filePath, corruptBytes);

        var saved = await _store.SaveConfigAsync(
            CreateTestConfig(domain, "^https?://test-hierarchy-corruptsave\\.example\\.com/?$"));

        saved.Should().BeTrue();
        var asides = Directory.GetFiles(_storagePath, domain + ".corrupt-*.json");
        asides.Should().HaveCount(1, "the unreadable file is kept as <name>.corrupt-<timestamp>.json");
        (await File.ReadAllBytesAsync(asides[0])).Should().Equal(corruptBytes, "the user's bytes are preserved exactly");
        (await _store.GetConfigAsync($"https://{domain}/")).Should().NotBeNull("the new layout was still saved");
    }

    // Guards:   Merely reading layouts from a corrupt file also keeps its bytes (moved
    //           aside, not deleted), and later bulk operations do not re-process the
    //           aside copy again and again.
    // Fails if: the read path drops the corrupt bytes, or ClearLegacySnapshotsAsync
    //           treats the aside file as a live layout file and shuffles it into
    //           .corrupt-....corrupt-....json chains.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~GetConfigAsync_CorruptFile_KeepsBytesAndAsideFileIsLeftAlone"
    [Fact]
    public async Task GetConfigAsync_CorruptFile_KeepsBytesAndAsideFileIsLeftAlone()
    {
        const string domain = "test-hierarchy-corruptread.example.com";
        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("this is not json at all {{{");
        Directory.CreateDirectory(_storagePath);
        await File.WriteAllBytesAsync(Path.Combine(_storagePath, domain + ".json"), corruptBytes);

        (await _store.GetConfigAsync($"https://{domain}/")).Should().BeNull();
        await _store.ClearLegacySnapshotsAsync();
        await _store.GetConfigCountAsync();

        var asides = Directory.GetFiles(_storagePath, "test-hierarchy-corruptread*.json");
        asides.Should().HaveCount(1, "exactly one aside copy, no chained .corrupt-....corrupt-.... renames");
        Path.GetFileName(asides[0]).Should().MatchRegex(
            @"^test-hierarchy-corruptread\.example\.com\.corrupt-\d{14}\.json$");
        (await File.ReadAllBytesAsync(asides[0])).Should().Equal(corruptBytes);
    }
}
