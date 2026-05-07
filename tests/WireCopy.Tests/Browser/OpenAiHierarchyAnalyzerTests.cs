// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Chat;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for <see cref="OpenAiHierarchyAnalyzer"/> — the OpenAI-backed
/// replacement for the previous Anthropic analyzer (workspace-65sw).
/// All API calls are intercepted via the analyzer's <c>ChatCompleter</c>
/// test seam so no live network traffic is required.
/// </summary>
[Trait("Category", "Unit")]
public class OpenAiHierarchyAnalyzerTests
{
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<OpenAiHierarchyAnalyzer> _logger;
    private readonly OpenAiHierarchyConfiguration _config;
    private readonly OpenAiTtsConfiguration _ttsConfig;

    public OpenAiHierarchyAnalyzerTests()
    {
        _settingsStore = Substitute.For<IUserSettingsStore>();
        _logger = Substitute.For<ILogger<OpenAiHierarchyAnalyzer>>();
        _config = new OpenAiHierarchyConfiguration
        {
            Model = "gpt-5-mini",
            ReasoningEffort = "minimal",
            MaxTokens = 4096,
        };
        _ttsConfig = new OpenAiTtsConfiguration();
    }

    private OpenAiHierarchyAnalyzer CreateAnalyzer(OpenAiHierarchyAnalyzer.ChatCompleter? completer = null)
    {
        return new OpenAiHierarchyAnalyzer(
            Options.Create(_config),
            Options.Create(_ttsConfig),
            _settingsStore,
            _logger,
            completer);
    }

    private static List<LinkInfo> CreateSampleLinks()
    {
        return new List<LinkInfo>
        {
            new LinkInfo
            {
                Url = "https://example.com/article1",
                DisplayText = "Article 1",
                Type = LinkType.Content,
                ImportanceScore = 80,
                ParentSelector = "article > h3",
            },
            new LinkInfo
            {
                Url = "https://example.com/article2",
                DisplayText = "Article 2",
                Type = LinkType.Content,
                ImportanceScore = 70,
            },
        };
    }

    [Fact]
    public void IsConfigured_NoApiKey_ReturnsFalse()
    {
        _settingsStore.Get("OpenAiApiKey").Returns((string?)null);

        var analyzer = CreateAnalyzer();

        analyzer.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ApiKeyInSettingsStore_ReturnsTrue()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var analyzer = CreateAnalyzer();

        analyzer.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_ApiKeyOnTtsConfig_ReturnsTrue()
    {
        _settingsStore.Get("OpenAiApiKey").Returns((string?)null);
        var analyzer = new OpenAiHierarchyAnalyzer(
            Options.Create(_config),
            Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-config-key" }),
            _settingsStore,
            _logger);

        analyzer.IsConfigured.Should().BeTrue(
            "the analyzer reuses the TTS-bound API key when no settings-store override is present");
    }

    [Fact]
    public void IsConfigured_SettingsStoreWinsOverConfig()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-store-key");
        var analyzer = new OpenAiHierarchyAnalyzer(
            Options.Create(_config),
            Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-config-key" }),
            _settingsStore,
            _logger);

        analyzer.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_NoApiKey_ThrowsInvalidOperation()
    {
        _settingsStore.Get("OpenAiApiKey").Returns((string?)null);
        var analyzer = CreateAnalyzer();

        var act = () => analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*API key not configured*");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_StrictSchemaResponse_Parses()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, _, _, _) =>
            Task.FromResult(
                "{\"sections\":[{\"name\":\"Top Stories\",\"linkIndices\":[0,1]," +
                "\"parentSelectors\":[\"article > h3\"],\"urlPatterns\":[\"/article/\"]," +
                "\"startCollapsed\":false}]}");

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        result.Should().NotBeNull();
        result.Domain.Should().Be("example.com");
        result.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Top Stories");
        result.Sections[0].ParentSelectors.Should().Contain("article > h3");
        result.ModelVersion.Should().Be("gpt-5-mini");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_LegacyBareArrayShape_StillParses()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        // The strict schema wraps in {"sections":[...]} but the parser also
        // tolerates a bare-array response so a model glitch on retry doesn't
        // hard-fail the request.
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, _, _, _) =>
            Task.FromResult(
                "[{\"name\":\"Main\",\"linkIndices\":[0,1],\"parentSelectors\":[]," +
                "\"urlPatterns\":[],\"startCollapsed\":false}]");

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        result.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Main");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_MarkdownFences_StripsAndParses()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, _, _, _) =>
            Task.FromResult(
                "```json\n{\"sections\":[{\"name\":\"Section\",\"linkIndices\":[0]," +
                "\"parentSelectors\":[],\"urlPatterns\":[],\"startCollapsed\":false}]}\n```");

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        result.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Section");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_MalformedJson_RetriesThenSucceeds()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var attempt = 0;
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, _, _, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                return Task.FromResult("not valid JSON at all");
            }

            return Task.FromResult(
                "{\"sections\":[{\"name\":\"Recovered\",\"linkIndices\":[0]," +
                "\"parentSelectors\":[],\"urlPatterns\":[],\"startCollapsed\":false}]}");
        };

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        attempt.Should().Be(2, "the analyzer must retry once on malformed JSON");
        result.Sections[0].Name.Should().Be("Recovered");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_UrlPattern_IncludesDomain()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, _, _, _) =>
            Task.FromResult("{\"sections\":[{\"name\":\"Main\",\"linkIndices\":[0]," +
                "\"parentSelectors\":[],\"urlPatterns\":[],\"startCollapsed\":false}]}");

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://www.nytimes.com/section/opinion");

        result.UrlPattern.Should().Contain("nytimes\\.com");
        result.Domain.Should().Be("www.nytimes.com");
    }

    [Fact]
    public async Task AnalyzeCuratedAsync_HappyPath_ParsesAndMapsKeys()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, _, _, _) =>
            Task.FromResult(
                "{\"excluded\":[1],\"stories\":[0]," +
                "\"sections\":[{\"name\":\"Lead\",\"story_indices\":[0],\"start_collapsed\":false}]}");

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.AnalyzeCuratedAsync(
            screenshot: null,
            CreateSampleLinks(),
            "https://example.com/");

        result.Should().NotBeNull();
        result.ExcludedLinkKeys.Should().HaveCount(1);
        result.StoryOrderLinkKeys.Should().HaveCount(1);
        result.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Lead");
    }

    [Fact]
    public async Task AnalyzeCuratedAsync_EmptySections_DropsThem()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, _, _, _) =>
            Task.FromResult(
                "{\"excluded\":[],\"stories\":[0,1]," +
                "\"sections\":[{\"name\":\"Empty\",\"story_indices\":[],\"start_collapsed\":false}]}");

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.AnalyzeCuratedAsync(
            screenshot: null,
            CreateSampleLinks(),
            "https://example.com/");

        result.Sections.Should().BeEmpty(
            "sections that resolve to zero valid keys are dropped to keep the curated tree clean");
        result.StoryOrderLinkKeys.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnalyzeCuratedAsync_NoApiKey_ThrowsInvalidOperation()
    {
        _settingsStore.Get("OpenAiApiKey").Returns((string?)null);
        var analyzer = CreateAnalyzer();

        var act = () => analyzer.AnalyzeCuratedAsync(
            screenshot: null,
            CreateSampleLinks(),
            "https://example.com/");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*API key not configured*");
    }

    [Fact]
    public void ParseHierarchyResponse_StaticHelper_HandlesEnvelopeShape()
    {
        var json = "{\"sections\":[{\"name\":\"S\",\"linkIndices\":[]," +
            "\"parentSelectors\":[],\"urlPatterns\":[],\"startCollapsed\":true}]}";

        var result = OpenAiHierarchyAnalyzer.ParseHierarchyResponse(
            json, "example.com", "https://example.com/", "gpt-5-mini");

        result.Sections.Should().HaveCount(1);
        result.Sections[0].StartCollapsed.Should().BeTrue();
        result.ModelVersion.Should().Be("gpt-5-mini");
    }

    [Fact]
    public void ParseCuratedResponse_StaticHelper_FiltersOutOfRangeIndices()
    {
        var json = "{\"excluded\":[99],\"stories\":[0,1,42],\"sections\":[]}";

        var result = OpenAiHierarchyAnalyzer.ParseCuratedResponse(
            json, CreateSampleLinks());

        result.ExcludedLinkKeys.Should().BeEmpty(
            "out-of-range indices map to empty keys and are filtered");
        result.StoryOrderLinkKeys.Should().HaveCount(2,
            "only the two valid in-range indices survive");
    }
}
