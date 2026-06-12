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
    public async Task AnalyzeCuratedAsync_EnrichedPrompt_IncludesScoreSectionAndPathOnlyUrls()
    {
        // workspace-5oe9.4: the curated user prompt must surface ImportanceScore
        // and SectionTitle (the two prominence signals) and strip scheme+host
        // from link URLs so the model sees path patterns, not host noise.
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        List<ChatMessage>? captured = null;
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, messages, _, _) =>
        {
            captured = messages.ToList();
            return Task.FromResult("{\"excluded\":[],\"stories\":[0,1],\"sections\":[]}");
        };

        var analyzer = CreateAnalyzer(completer);
        var links = new List<LinkInfo>
        {
            new LinkInfo
            {
                Url = "https://example.com/article1",
                DisplayText = "Article 1",
                Type = LinkType.Content,
                ImportanceScore = 80,
                ParentSelector = "section.lead > a",
                SectionTitle = "Top Story",
            },
            new LinkInfo
            {
                Url = "https://example.com/article2",
                DisplayText = "Article 2",
                Type = LinkType.Content,
                ImportanceScore = 40,
            },
        };

        await analyzer.AnalyzeCuratedAsync(screenshot: null, links, "https://example.com/");

        var userText = string.Join(
            "\n",
            captured!.SelectMany(m => m.Content)
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

        userText.Should().Contain("score=80");
        userText.Should().Contain("sect=\"Top Story\"");
        userText.Should().Contain("/article1");
        userText.Should().NotContain("https://example.com/article1",
            "link URLs are emitted path-only to save tokens and surface path patterns");
    }

    [Fact]
    public async Task AnalyzeCuratedAsync_AggregatorPage_KeepsHostsAndAddsAggregatorNote()
    {
        // workspace-6yb7.2: on an aggregator (majority of content links external)
        // the prompt must keep each story's host (the host IS signal) and warn the
        // model that URL patterns will not generalize.
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        List<ChatMessage>? captured = null;
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, messages, _, _) =>
        {
            captured = messages.ToList();
            return Task.FromResult("{\"excluded\":[],\"stories\":[0],\"sections\":[]}");
        };

        var analyzer = CreateAnalyzer(completer);
        var links = Enumerable.Range(1, 12).Select(i => new LinkInfo
        {
            Url = $"https://publisher{i}.com/post/{i}",
            DisplayText = $"Story {i}",
            Type = LinkType.Content,
            ImportanceScore = 70,
            ParentSelector = "div.clus div.ourh",
            IsExternal = true,
        }).ToList();

        await analyzer.AnalyzeCuratedAsync(screenshot: null, links, "https://aggregator.example/");

        var userText = string.Join(
            "\n",
            captured!.SelectMany(m => m.Content)
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

        userText.Should().Contain("publisher1.com/post/1",
            "external story links keep their host in the prompt");
        userText.Should().Contain("AGGREGATOR",
            "the aggregator note steers the model away from URL patterns");
        userText.Should().Contain("parent CSS");
    }

    [Fact]
    public async Task AnalyzeCuratedAsync_ConventionalPage_NoAggregatorNote()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        List<ChatMessage>? captured = null;
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, messages, _, _) =>
        {
            captured = messages.ToList();
            return Task.FromResult("{\"excluded\":[],\"stories\":[0,1],\"sections\":[]}");
        };

        var analyzer = CreateAnalyzer(completer);
        await analyzer.AnalyzeCuratedAsync(screenshot: null, CreateSampleLinks(), "https://example.com/");

        var userText = string.Join(
            "\n",
            captured!.SelectMany(m => m.Content)
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

        userText.Should().NotContain("AGGREGATOR");
    }

    [Theory]
    [InlineData(12, 8, true)]   // 2/3 external, over the shared 0.6 share bar
    [InlineData(12, 7, false)]  // 58% external is below LinkExtractor.AggregatorExternalShare
    [InlineData(9, 9, false)]   // under LinkExtractor.AggregatorMinStoryLinks
    public void IsAggregatorLinkSet_Thresholds(int total, int external, bool expected)
    {
        var links = Enumerable.Range(0, total).Select(i => new LinkInfo
        {
            Url = $"https://site{i}.com/x",
            DisplayText = $"Link {i}",
            Type = LinkType.Content,
            ImportanceScore = 50,
            IsExternal = i < external,
        }).ToList();

        OpenAiHierarchyAnalyzer.IsAggregatorLinkSet(links).Should().Be(expected);
    }

    [Fact]
    public async Task AnalyzeCuratedAsync_ReasoningEffortOverride_ReachesOptions()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        ChatCompletionOptions? capturedWith = null;
        ChatCompletionOptions? capturedWithout = null;

        var analyzerWith = CreateAnalyzer((_, _, _, options, _) =>
        {
            capturedWith = options;
            return Task.FromResult("{\"excluded\":[],\"stories\":[0,1],\"sections\":[]}");
        });
        await analyzerWith.AnalyzeCuratedAsync(
            screenshot: null, CreateSampleLinks(), "https://example.com/", reasoningEffortOverride: "low");

        var analyzerWithout = CreateAnalyzer((_, _, _, options, _) =>
        {
            capturedWithout = options;
            return Task.FromResult("{\"excluded\":[],\"stories\":[0,1],\"sections\":[]}");
        });
        await analyzerWithout.AnalyzeCuratedAsync(
            screenshot: null, CreateSampleLinks(), "https://example.com/");

#pragma warning disable OPENAI001 // Experimental reasoning-effort surface
        capturedWith!.ReasoningEffortLevel.Should().Be(ChatReasoningEffortLevel.Low,
            "an explicit override must reach the request options");
        capturedWithout!.ReasoningEffortLevel.Should().Be(ChatReasoningEffortLevel.Minimal,
            "with no override the configured default ('minimal') is used");
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ParseCuratedResponse_IntegerIndexMapping_Unchanged()
    {
        // Regression guard (workspace-5oe9.4): the prompt enrichment must NOT
        // disturb the excluded/stories integer[] -> URL-key mapping that
        // DetectDegenerateRanking and BuildFromAiCuratedResult depend on.
        var json = "{\"excluded\":[1],\"stories\":[0],\"sections\":[]}";

        var result = OpenAiHierarchyAnalyzer.ParseCuratedResponse(json, CreateSampleLinks());

        result.ExcludedLinkKeys.Should().ContainSingle()
            .Which.Should().Be(AiCuratedResult.KeyFor("https://example.com/article2"));
        result.StoryOrderLinkKeys.Should().ContainSingle()
            .Which.Should().Be(AiCuratedResult.KeyFor("https://example.com/article1"));
    }

    // ---- workspace-5oe9.7: clarifying-questions model contract ----

    private static List<LinkInfo> SetupLinks() => new()
    {
        new LinkInfo { Url = "https://x.com/lead", DisplayText = "Lead", Type = LinkType.Content, ImportanceScore = 95, ParentSelector = "section.lead a" },
        new LinkInfo { Url = "https://x.com/feed-1", DisplayText = "Feed 1", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "section.feed a" },
        new LinkInfo { Url = "https://x.com/feed-2", DisplayText = "Feed 2", Type = LinkType.Content, ImportanceScore = 65, ParentSelector = "section.feed a" },
        new LinkInfo { Url = "https://x.com/promo", DisplayText = "Subscribe", Type = LinkType.Content, ImportanceScore = 20, ParentSelector = "aside.promo a" },
    };

    private const string InferPatternJson =
        "{\"sections\":[" +
        "{\"name\":\"Top Story\",\"parent_selectors\":[\"section.lead\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}," +
        "{\"name\":\"Feed\",\"parent_selectors\":[\"section.feed\"],\"url_patterns\":[],\"story_indices\":[1,2],\"start_collapsed\":false}]," +
        "\"exclude_selectors\":[\"aside.promo\"],\"exclude_url_patterns\":[],\"exclude_indices\":[3]}";

    private static string ProposalJson(int questionCount)
    {
        var qs = string.Join(",", Enumerable.Range(0, questionCount).Select(i =>
            $"{{\"id\":\"q{i}\",\"prompt\":\"Question {i}?\",\"kind\":\"pick_main\",\"default_answer\":\"yes\"," +
            "\"options\":[{\"label\":\"Opt\",\"parent_selector\":\"section.lead\",\"url_pattern\":\"\",\"example_link_indices\":[0]}]}"));
        return "{\"proposed_pattern\":{" +
            "\"top_story\":{\"label\":\"Lead\",\"parent_selector\":\"section.lead\",\"url_pattern\":\"\",\"example_link_indices\":[0]}," +
            "\"tiers\":[{\"label\":\"Feed\",\"parent_selector\":\"section.feed\",\"url_pattern\":\"\",\"example_link_indices\":[1,2]}]," +
            "\"exclude\":[{\"label\":\"Ads\",\"parent_selector\":\"aside.promo\",\"url_pattern\":\"\",\"example_link_indices\":[3]}]}," +
            $"\"questions\":[{qs}]}}";
    }

    [Fact]
    public async Task ProposeSetupQuestionsAsync_ClampsToMaxQuestions()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        // MaxSetupQuestions defaults to 4.

        var analyzer = CreateAnalyzer((_, _, _, _, _) => Task.FromResult(ProposalJson(20)));

        var proposal = await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");

        proposal.Questions.Should().HaveCountLessThanOrEqualTo(4, "the question count is clamped in code");
        proposal.ProposedPattern.TopStory!.ParentSelector.Should().Be("section.lead");
    }

    [Fact]
    public async Task InferPatternFromAnswersAsync_ReturnsSelectorSections_NotUrlKeys_AndPassesAnswersIntoPrompt()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        List<ChatMessage>? captured = null;
        var analyzer = CreateAnalyzer((_, _, messages, _, _) =>
        {
            captured = messages.ToList();
            return Task.FromResult(InferPatternJson);
        });

        var proposal = new SiteSetupProposal
        {
            ProposedPattern = new ProposedPattern(),
            Questions = new List<SetupQuestion>
            {
                new() { Id = "q0", Prompt = "Which is the lead story?", Kind = SetupQuestionKind.PickMain, DefaultAnswer = "Lead" },
            },
        };
        var answers = new List<SetupAnswer> { new() { QuestionId = "q0", Answer = "the budget vote story" } };

        var config = await analyzer.InferPatternFromAnswersAsync(null, SetupLinks(), "https://x.com/", proposal, answers);

        config.Version.Should().Be(3);
        config.Sections.Should().HaveCount(2);
        config.Sections.SelectMany(s => s.ParentSelectors).Should().Contain("section.lead").And.Contain("section.feed");
        config.Sections.SelectMany(s => s.ParentSelectors).Should().NotContain(s => s.StartsWith("url:", StringComparison.Ordinal));
        config.ExcludeSelectors.Should().Contain("aside.promo");

        var userText = string.Join("\n", captured!.SelectMany(m => m.Content)
            .Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
        userText.Should().Contain("the budget vote story", "the user's answer must reach the round-2 prompt");
    }

    [Fact]
    public async Task SetupContract_AcceptAllPath_PerformsExactlyTwoRoundTrips()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var calls = 0;
        var analyzer = CreateAnalyzer((_, _, _, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? ProposalJson(2) : InferPatternJson);
        });

        var proposal = await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");
        var answers = proposal.Questions.Select(q => new SetupAnswer { QuestionId = q.Id, Answer = q.DefaultAnswer }).ToList();
        await analyzer.InferPatternFromAnswersAsync(null, SetupLinks(), "https://x.com/", proposal, answers);

        calls.Should().Be(2, "the setup contract is exactly two model round-trips: propose + infer");
    }

    [Fact]
    public async Task ProposeSetupQuestionsAsync_MalformedJson_RetriesThenSucceeds()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var attempt = 0;
        var analyzer = CreateAnalyzer((_, _, _, _, _) =>
        {
            attempt++;
            return Task.FromResult(attempt == 1 ? "not json" : ProposalJson(1));
        });

        var proposal = await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");

        attempt.Should().Be(2);
        proposal.Questions.Should().ContainSingle();
    }

    [Fact]
    public void ParseSetupQuestions_ClampsQuestionsOptionsAndIndices()
    {
        // 20 questions in, default cap 4 out; option/index clamps applied.
        var result = OpenAiHierarchyAnalyzer.ParseSetupQuestions(ProposalJson(20), maxQuestions: 4);
        result.Questions.Should().HaveCount(4);
    }

    [Fact]
    public void ParsePatternFromAnswers_DerivesSelectorsWhenModelOmitsThem()
    {
        // Model returns story_indices but EMPTY selectors → B3 fallback derives them.
        var json =
            "{\"sections\":[{\"name\":\"Lead\",\"parent_selectors\":[],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[3]}";

        var config = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, SetupLinks(), "https://x.com/", "gpt-5-mini");

        config.Sections.Should().ContainSingle();
        config.Sections[0].ParentSelectors.Should().Contain("section.lead", "derived as a B3 fallback from story_indices");
        config.ExcludeSelectors.Should().Contain("aside.promo");
    }

    [Fact]
    public void EstimateSetupOutputTokens_ScalesWithQuestionCapAndStaysGenerous()
    {
        OpenAiHierarchyAnalyzer.EstimateSetupOutputTokens(0).Should().BeGreaterThan(0);
        OpenAiHierarchyAnalyzer.EstimateSetupOutputTokens(4)
            .Should().BeGreaterThan(OpenAiHierarchyAnalyzer.EstimateSetupOutputTokens(1));
    }

    [Fact]
    public async Task ProposeSetupQuestionsAsync_RequestsAtLeastTheEstimatedTokenBudget()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        // MaxSetupQuestions defaults to 4.

        ChatCompletionOptions? captured = null;
        var analyzer = CreateAnalyzer((_, _, _, options, _) =>
        {
            captured = options;
            return Task.FromResult(ProposalJson(2));
        });

        await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");

        captured!.MaxOutputTokenCount.Should()
            .BeGreaterThanOrEqualTo(OpenAiHierarchyAnalyzer.EstimateSetupOutputTokens(4));
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
