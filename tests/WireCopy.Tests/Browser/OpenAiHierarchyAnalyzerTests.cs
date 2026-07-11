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

    [Theory]
    [InlineData("Right rail — Sponsor Posts")]
    [InlineData("Right-rail promos / sponsor column (keep out)")]
    [InlineData("Featured podcasts (sidebar)")]
    [InlineData("Right rail — podcasts (promo widgets)")]
    [InlineData("Events / calendar (promo)")]
    [InlineData("Audio / listen")]
    public void IsNonStoryRailSectionName_RailNames_ReturnTrue(string name)
    {
        OpenAiHierarchyAnalyzer.IsNonStoryRailSectionName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Top story")]
    [InlineData("Main column — prominent headlines")]
    [InlineData("Main river (other large headlines)")]
    [InlineData("Newest / time-sorted list")]
    [InlineData("Opinion")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNonStoryRailSectionName_StorySections_ReturnFalse(string? name)
    {
        OpenAiHierarchyAnalyzer.IsNonStoryRailSectionName(name).Should().BeFalse();
    }

    [Fact]
    public async Task RefineLayoutAsync_PromptCarriesCurrentLayoutAndChange_AndPreserves()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var current = new SiteHierarchyConfig
        {
            Domain = "example.com",
            UrlPattern = "^https?://(www\\.)?example\\.com/?",
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "gpt-5-mini",
            Sections = new List<HierarchySection>
            {
                new() { Name = "Top story", SortOrder = 0, ParentSelectors = new() { "strong.L5" } },
                new() { Name = "Main feed", SortOrder = 1, ParentSelectors = new() { "div.ii > strong" } },
            },
            ExcludeSelectors = new List<string> { "div#events" },
        };

        var prompt = string.Empty;
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, messages, _, _) =>
        {
            foreach (var m in messages)
            {
                foreach (var part in m.Content)
                {
                    if (part.Kind == ChatMessageContentPartKind.Text)
                    {
                        prompt += part.Text + "\n";
                    }
                }
            }

            // The model echoes the two current sections back unchanged and only
            // ADDS the sponsor exclusion the instruction asked for.
            return Task.FromResult(
                "{\"sections\":[" +
                "{\"name\":\"Top story\",\"parent_selectors\":[\"strong.L5\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}," +
                "{\"name\":\"Main feed\",\"parent_selectors\":[\"div.ii > strong\"],\"url_patterns\":[],\"story_indices\":[1],\"start_collapsed\":false}]," +
                "\"exclude_selectors\":[\"div#events\",\"div#radymre\"],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}");
        };

        var analyzer = CreateAnalyzer(completer);

        var result = await analyzer.RefineLayoutAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/",
            current,
            "hide the sponsor posts");

        // workspace-45ji: the refine prompt shows the current layout as section
        // names + link-INDEX membership (not raw selectors) and the change.
        prompt.Should().Contain("Top story").And.Contain("Main feed").And.Contain("links [");
        prompt.Should().Contain("hide the sponsor posts");
        prompt.ToLowerInvariant().Should().Contain("keep");
        prompt.Should().Contain("CURRENT LAYOUT");

        // The two working sections survive; only the requested exclusion is added.
        result.Config.Sections.Select(s => s.Name).Should().Equal("Top story", "Main feed");
        result.Config.ExcludeSelectors.Should().Contain("div#radymre").And.Contain("div#events");
    }

    private static List<LinkInfo> StoryLinks(int count, int score, string parent) =>
        Enumerable.Range(0, count).Select(i => new LinkInfo
        {
            Url = $"https://x.com/story{i}",
            DisplayText = $"Story {i}",
            Type = LinkType.Content,
            ImportanceScore = score,
            ParentSelector = parent,
        }).ToList();

    [Fact]
    public void ParsePatternFromAnswers_OverBroadExclude_DroppedToProtectStories()
    {
        // workspace-9wm6: 4 high-importance stories under 'div.river'; the model
        // wrongly excludes the whole container. The guard must drop that rule.
        var links = StoryLinks(4, score: 90, parent: "div.river > div.item");
        var json =
            "{\"sections\":[{\"name\":\"Feed\",\"parent_selectors\":[\"div.river\"],\"url_patterns\":[]," +
            "\"story_indices\":[0,1,2,3],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[\"div.river\",\"div#ad\"],\"exclude_url_patterns\":[]," +
            "\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.ExcludeSelectors.Should().NotContain("div.river", "it would hide all 4 stories");
        result.Config.ExcludeSelectors.Should().Contain("div#ad", "surgical excludes hitting no stories survive");
    }

    [Fact]
    public void ParsePatternFromAnswers_ExcludeHittingRenderedStory_DroppedEvenUnderProportionalFloor()
    {
        // workspace-v2m8.5: exclude patterns match by SUBSTRING over the full
        // URL. A model-emitted hub segment ('/technology/') hides a cross-domain
        // story the config's OWN section renders — self-contradictory — yet it
        // slipped under the proportional guard's floor of 2 (thirteen such
        // patterns helped collapse the 2026-07-07 techmeme run to 31/122
        // coverage). A rail pattern hitting no section-rendered story survives.
        var links = StoryLinks(6, score: 90, parent: "div.river > div.item");
        links[0] = links[0] with { Url = "https://nytimes.com/technology/some-real-story" };
        links.Add(new LinkInfo
        {
            Url = "https://x.com/r2/podcast-episode",
            DisplayText = "Podcast episode",
            Type = LinkType.Content,
            ImportanceScore = 85,
            ParentSelector = "div.rail > div.pod",
        });
        var json =
            "{\"sections\":[{\"name\":\"Feed\",\"parent_selectors\":[\"div.river\"],\"url_patterns\":[]," +
            "\"story_indices\":[0,1,2,3,4,5],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[\"/technology/\",\"/r2/\"]," +
            "\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.ExcludeUrlPatterns.Should().NotContain("/technology/",
            "it hides a high-importance story the Feed section renders");
        result.Config.ExcludeUrlPatterns.Should().Contain("/r2/",
            "the podcast rail is matched by no kept section — still excludable");
        result.Config.DroppedExcludeRuleCount.Should().BeGreaterThanOrEqualTo(1,
            "the veto is surfaced on the preview footnote, not silent");
    }

    [Fact]
    public void ParsePatternFromAnswers_BroadLead_KeepsSiblings_NotPinnedToOne()
    {
        // workspace-5vqk.2: a pick teaches a REPEATING pattern, so the first section
        // is never force-collapsed to one story. Even though the lead selector
        // matches all 3 headlines and a later Feed section also claims the overflow,
        // the parser no longer pins MaxLinks=1 — the co-equal siblings stay in the
        // lead section (the flat-aggregator "one story instead of many" fix).
        var links = StoryLinks(3, score: 85, parent: "div.col > div.item > strong");
        var json =
            "{\"sections\":[" +
            "{\"name\":\"Lead\",\"parent_selectors\":[\"div.item > strong\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}," +
            "{\"name\":\"Feed\",\"parent_selectors\":[\"div.col > div.item\"],\"url_patterns\":[],\"story_indices\":[1,2],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections[0].MaxLinks.Should().BeNull("a pick seeds a co-equal river, never a single pinned lead");
    }

    [Fact]
    public void ParsePatternFromAnswers_RailNamedFirstSection_DemotedWhenARealSectionFollows()
    {
        // workspace-ycdc: a rail-named FIRST section used to be protected by the
        // never-demote-the-lead guard, so a "Promos" lead leaked ahead of the real
        // feed. It must now be demoted to the excludes and the real section leads.
        var links = StoryLinks(4, score: 90, parent: "div.main > div.item");
        links.Add(links[0] with { Url = "https://x.com/r2/promo", ParentSelector = "div.promocol > div.ad" });
        var json =
            "{\"sections\":[" +
            "{\"name\":\"From Mediagazer / Promos\",\"parent_selectors\":[\"div.promocol\"],\"url_patterns\":[],\"story_indices\":[4],\"start_collapsed\":false}," +
            "{\"name\":\"Headlines\",\"parent_selectors\":[\"div.main\"],\"url_patterns\":[],\"story_indices\":[0,1,2,3],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections.Should().ContainSingle("the rail-named section was demoted, leaving the real feed");
        result.Config.Sections[0].ParentSelectors.Should().Contain("div.main", "the real Headlines feed is the lead now");
        result.Config.Sections.Should().NotContain(s => s.ParentSelectors.Contains("div.promocol"),
            "the rail must never render as a section, lead or otherwise");
        result.Config.ExcludeSelectors.Should().Contain("div.promocol", "the demoted rail's identifier is excluded");
    }

    [Fact]
    public void ParsePatternFromAnswers_RailNamedSoleSection_IsDegenerate_FallsToFlat()
    {
        // workspace-ycdc: the 44/132 techmeme run — the model's ONLY section was
        // rail-named (it ignored the real feed). Rather than render that junk as the
        // layout, demote it: zero real sections -> HierarchyRouteResolver falls to
        // the flat document-order guardrail (all real stories in page order), with
        // the surgical rail identifier excluded. The real stories exist as links the
        // model failed to section — exactly the field case.
        var links = StoryLinks(6, score: 90, parent: "div.main > div.item"); // the real feed the model ignored
        links.Add(new LinkInfo
        {
            Url = "https://x.com/r2/promo-widget",
            DisplayText = "Sponsored promo",
            Type = LinkType.Content,
            ImportanceScore = 40, // a rail widget, not a high-importance story
            ParentSelector = "div.promocol > div.ad",
        });
        var json =
            "{\"sections\":[" +
            "{\"name\":\"From Mediagazer / Promos\",\"parent_selectors\":[\"div.promocol\"],\"url_patterns\":[],\"story_indices\":[6],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections.Should().BeEmpty("a rail-named sole section is degenerate — no junk lead renders");
        result.Config.ExcludeSelectors.Should().Contain("div.promocol",
            "the surgical rail identifier (hits no high-importance story) is routed to the excludes");
    }

    [Fact]
    public void ParsePatternFromAnswers_RealSections_NotDemoted()
    {
        // Guard against over-demotion: genuine story sections must survive.
        var links = StoryLinks(4, score: 90, parent: "div.main > div.item");
        var json =
            "{\"sections\":[" +
            "{\"name\":\"Lead story\",\"parent_selectors\":[\"div.lead\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}," +
            "{\"name\":\"Top headlines\",\"parent_selectors\":[\"div.main\"],\"url_patterns\":[],\"story_indices\":[1,2,3],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections.Should().HaveCount(2, "neither real section trips the rail regex");
    }

    [Theory]
    [InlineData("top_story", "Top stories")]      // schema-key at index 0 -> positional default
    [InlineData("", "Top stories")]               // empty -> positional default
    [InlineData("opinion", "Opinion")]            // lone lowercase word -> title-cased
    [InlineData("Lead cluster", "Lead cluster")]  // a real label is kept
    public void ParsePatternFromAnswers_HumanizesSchemaKeySectionNames(string modelName, string expected)
    {
        // workspace-r8on: qwen2.5:3b named its sections 'top_story'/'second_tier'
        // (the schema vocabulary), which then showed as the link-list header. The
        // parser must produce a reader-facing label regardless of the model.
        var links = StoryLinks(3, score: 85, parent: "div.ii");
        var json =
            "{\"sections\":[" +
            $"{{\"name\":\"{modelName}\",\"parent_selectors\":[\"div.ii\"],\"url_patterns\":[],\"story_indices\":[0,1,2],\"start_collapsed\":false}}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections.Should().ContainSingle().Which.Name.Should().Be(expected);
    }

    [Fact]
    public void ParsePatternFromAnswers_SecondSchemaKeySection_BecomesMoreStories()
    {
        // Two schema-key sections -> positional labels 'Top stories' + 'More stories'.
        var links = StoryLinks(3, score: 85, parent: "div.col");
        var json =
            "{\"sections\":[" +
            "{\"name\":\"top_story\",\"parent_selectors\":[\"div.ii\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}," +
            "{\"name\":\"second_tier\",\"parent_selectors\":[\"div.ed\"],\"url_patterns\":[],\"story_indices\":[1,2],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections.Select(s => s.Name).Should().Equal("Top stories", "More stories");
        result.Config.Sections.Should().NotContain(s => s.Name.Contains('_'), "no schema-key name reaches the link list");
    }

    [Fact]
    public void ParsePatternFromAnswers_DropsSubsumedDuplicateSection()
    {
        // workspace-r8on: two sections with the SAME selector match the same
        // stories — the second is subsumed (first-match) and must NOT be saved as a
        // dead section (the techmeme judge's duplicate 'Lead cluster' + 'co-equal').
        var links = StoryLinks(3, score: 85, parent: "div.col > div.ii");
        var json =
            "{\"sections\":[" +
            "{\"name\":\"Lead cluster\",\"parent_selectors\":[\"div.ii\"],\"url_patterns\":[],\"story_indices\":[0,1,2],\"start_collapsed\":false}," +
            "{\"name\":\"Top stories - co-equal\",\"parent_selectors\":[\"div.ii\"],\"url_patterns\":[],\"story_indices\":[0,1,2],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections.Should().ContainSingle("the subsumed duplicate section is dropped");
        result.Config.Sections[0].Name.Should().Be("Lead cluster");
    }

    [Fact]
    public void ParsePatternFromAnswers_BroadLead_NotPinned_WhenOverflowWouldBeLost()
    {
        // Lead matches all 3; the Feed section matches none of them, so capping the
        // lead would HIDE two stories — leave the lead uncapped.
        var links = StoryLinks(3, score: 85, parent: "div.col > div.item > strong");
        var json =
            "{\"sections\":[" +
            "{\"name\":\"Lead\",\"parent_selectors\":[\"div.item > strong\"],\"url_patterns\":[],\"story_indices\":[0],\"start_collapsed\":false}," +
            "{\"name\":\"Feed\",\"parent_selectors\":[\"div.unrelated\"],\"url_patterns\":[],\"story_indices\":[],\"start_collapsed\":false}]," +
            "\"exclude_selectors\":[],\"exclude_url_patterns\":[],\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}";

        var result = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, links, "https://x.com/", "gpt-5-mini");

        result.Config.Sections[0].MaxLinks.Should().BeNull();
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
        result.ModelVersion.Should().Be("gpt-5-nano"); // workspace-r8on: judge default
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

        // workspace-42q8.1: the storage key is www-stripped so both host variants
        // of the same site share one config.
        result.Domain.Should().Be("nytimes.com");
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
        result.ModelVersion.Should().Be("gpt-5-mini", "the static helper echoes the passed model version");
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
    public async Task ProposeSetupQuestionsAsync_IndicesOnlyOption_DerivesSelectorInCode()
    {
        // workspace-45ji: the model returns options with example_link_indices ONLY
        // (no parent_selector/url_pattern) — the durable identifier is derived from
        // the example links in code (ParseSetupQuestions/MapOption).
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        var json = "{\"proposed_pattern\":{" +
                   "\"top_story\":{\"label\":\"Lead\",\"example_link_indices\":[0]}," +
                   "\"tiers\":[{\"label\":\"Feed\",\"example_link_indices\":[1,2]}],\"exclude\":[]}," +
                   "\"questions\":[]}";
        var analyzer = CreateAnalyzer((_, _, _, _, _) => Task.FromResult(json));

        var proposal = await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");

        proposal.ProposedPattern.TopStory!.ParentSelector.Should().Be("section.lead",
            "derived from the lead link's parent selector, not emitted by the model");
        proposal.ProposedPattern.TopStory!.ExampleLinkIndices.Should().Contain(0);
        proposal.ProposedPattern.Tiers.Single().ParentSelector.Should().Be("section.feed");
    }

    [Fact]
    public async Task RefineLayoutAsync_IndicesOnlyResponse_DerivesSelectorsFromStoryIndices()
    {
        // workspace-45ji: refine returns indices only; code derives the section selectors.
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        var current = new SiteHierarchyConfig
        {
            Domain = "x.com",
            UrlPattern = "^https?://x\\.com/?",
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "gpt-5-mini",
            Sections = new List<HierarchySection> { new() { Name = "Top Story", SortOrder = 0, ParentSelectors = new() { "section.lead" } } },
        };
        var json = "{\"sections\":[{\"name\":\"Top Story\",\"story_indices\":[0],\"start_collapsed\":false}," +
                   "{\"name\":\"Feed\",\"story_indices\":[1,2],\"start_collapsed\":false}]," +
                   "\"exclude_indices\":[3],\"confidence\":0.9,\"confirm_question\":null}";
        var analyzer = CreateAnalyzer((_, _, _, _, _) => Task.FromResult(json));

        var config = (await analyzer.RefineLayoutAsync(null, SetupLinks(), "https://x.com/", current, "put the feed second")).Config;

        config.Sections.Should().HaveCount(2);
        config.Sections.SelectMany(s => s.ParentSelectors).Should().Contain("section.lead").And.Contain("section.feed");
        config.ExcludeSelectors.Should().Contain("aside.promo");
    }

    // ---- workspace-r8on: per-role models + local endpoint ----

    [Fact]
    public async Task JudgeModel_DefaultsToGpt5Nano_AndSettingsStoreOverrides()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        string? captured = null;
        var analyzer = CreateAnalyzer((_, model, _, _, _) => { captured = model; return Task.FromResult(ProposalJson(0)); });
        await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");
        captured.Should().Be("gpt-5-nano", "the judge default is gpt-5-nano");

        _settingsStore.Get("LayoutModel").Returns("custom-judge");
        string? captured2 = null;
        var analyzer2 = CreateAnalyzer((_, model, _, _, _) => { captured2 = model; return Task.FromResult(ProposalJson(0)); });
        await analyzer2.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");
        captured2.Should().Be("custom-judge", "the LayoutModel setting overrides the judge model");
    }

    [Fact]
    public async Task VerifyModel_ResolvesFromSettingsStore()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        _settingsStore.Get("VerifyModel").Returns("nano-vision");
        string? captured = null;
        var analyzer = CreateAnalyzer((_, model, _, _, _) => { captured = model; return Task.FromResult("{\"lead_index\":0}"); });

        await analyzer.VerifyLeadWithVisionAsync(new byte[] { 1 }, SetupLinks(), "https://x.com/");

        captured.Should().Be("nano-vision");
    }

    [Fact]
    public void IsConfigured_TrueWithLocalEndpoint_EvenWithoutAnOpenAiKey()
    {
        _settingsStore.Get("OpenAiApiKey").Returns((string?)null);
        _settingsStore.Get("LayoutEndpoint").Returns("http://localhost:11434/v1");
        CreateAnalyzer((_, _, _, _, _) => Task.FromResult(string.Empty)).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task LocalEndpoint_UsesOllamaKey_WithoutRequiringAnOpenAiKey()
    {
        _settingsStore.Get("OpenAiApiKey").Returns((string?)null);
        _settingsStore.Get("LayoutEndpoint").Returns("http://localhost:11434/v1");
        string? capturedKey = null;
        var analyzer = CreateAnalyzer((apiKey, _, _, _, _) => { capturedKey = apiKey; return Task.FromResult(ProposalJson(0)); });

        await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");

        capturedKey.Should().Be("ollama", "a local endpoint sends the dummy key, no OpenAI key needed");
    }

    [Fact]
    public async Task LocalEndpoint_WithCustomLayoutApiKey_SendsThatKey()
    {
        _settingsStore.Get("OpenAiApiKey").Returns((string?)null);
        _settingsStore.Get("LayoutEndpoint").Returns("http://localhost:11434/v1");
        _settingsStore.Get("LayoutApiKey").Returns("tok-123");
        string? capturedKey = null;
        var analyzer = CreateAnalyzer((apiKey, _, _, _, _) => { capturedKey = apiKey; return Task.FromResult(ProposalJson(0)); });

        await analyzer.ProposeSetupQuestionsAsync(null, SetupLinks(), "https://x.com/");

        capturedKey.Should().Be("tok-123");
    }

    [Fact]
    public async Task VerifyLeadWithVisionAsync_ReturnsModelIndex_WhenInRange()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        var analyzer = CreateAnalyzer((_, _, _, _, _) => Task.FromResult("{\"lead_index\":1}"));

        var idx = await analyzer.VerifyLeadWithVisionAsync(new byte[] { 1, 2, 3 }, SetupLinks(), "https://x.com/");

        idx.Should().Be(1);
    }

    [Theory]
    [InlineData("{\"lead_index\":99}")] // out of range → keep
    [InlineData("{\"lead_index\":-1}")] // model declined
    [InlineData("")]                     // empty completion
    public async Task VerifyLeadWithVisionAsync_KeepsCurrentLead_OnOutOfRangeOrDecline(string response)
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        var analyzer = CreateAnalyzer((_, _, _, _, _) => Task.FromResult(response));

        (await analyzer.VerifyLeadWithVisionAsync(new byte[] { 1 }, SetupLinks(), "https://x.com/")).Should().Be(-1);
    }

    [Fact]
    public async Task VerifyLeadWithVisionAsync_NoScreenshotOrTooFewCandidates_ShortCircuitsToMinusOne()
    {
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        var calls = 0;
        var analyzer = CreateAnalyzer((_, _, _, _, _) => { calls++; return Task.FromResult("{\"lead_index\":0}"); });

        (await analyzer.VerifyLeadWithVisionAsync(Array.Empty<byte>(), SetupLinks(), "https://x.com/")).Should().Be(-1);
        (await analyzer.VerifyLeadWithVisionAsync(new byte[] { 1 }, SetupLinks().Take(1).ToList(), "https://x.com/")).Should().Be(-1);
        calls.Should().Be(0, "no model call when there is nothing to judge");
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

        var config = (await analyzer.InferPatternFromAnswersAsync(null, SetupLinks(), "https://x.com/", proposal, answers)).Config;

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

        var config = OpenAiHierarchyAnalyzer.ParsePatternFromAnswers(json, SetupLinks(), "https://x.com/", "gpt-5-mini").Config;

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

    // ---- workspace-i1lv: reasoning-token starvation regression coverage ----

    private static SiteSetupProposal EmptyProposal() => new()
    {
        ProposedPattern = new ProposedPattern(),
        Questions = new List<SetupQuestion>(),
    };

    [Fact]
    public async Task InferPatternFromAnswersAsync_RequestsSetupMaxTokensCap()
    {
        // The infer-pattern call previously used the default 4096 cap (no override),
        // which reasoning tokens starved on gpt-5-mini. It must now request the
        // dedicated, larger setup cap.
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        ChatCompletionOptions? captured = null;
        var analyzer = CreateAnalyzer((_, _, _, options, _) =>
        {
            captured = options;
            return Task.FromResult(InferPatternJson);
        });

        await analyzer.InferPatternFromAnswersAsync(
            null, SetupLinks(), "https://x.com/", EmptyProposal(), new List<SetupAnswer>());

        captured!.MaxOutputTokenCount.Should()
            .BeGreaterThanOrEqualTo(_config.SetupMaxTokens,
                "the infer-pattern call must carry the reasoning-model setup cap, not the 4096 default");
    }

    [Fact]
    public async Task InferPatternFromAnswersAsync_EmptyCompletion_RetriesWithLargerCap_ThenSucceeds()
    {
        // An empty completion = reasoning burned max_completion_tokens before any
        // JSON. The retry must RAISE the cap (give reasoning room), not just re-send
        // the prompt — the old JsonException retry appended input and always failed.
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var caps = new List<int?>();
        var calls = 0;
        var analyzer = CreateAnalyzer((_, _, _, options, _) =>
        {
            calls++;
            caps.Add(options.MaxOutputTokenCount);
            return Task.FromResult(calls == 1 ? string.Empty : InferPatternJson);
        });

        var result = await analyzer.InferPatternFromAnswersAsync(
            null, SetupLinks(), "https://x.com/", EmptyProposal(), new List<SetupAnswer>());

        calls.Should().Be(2, "an empty (truncated) completion triggers exactly one retry");
        result.Config.Sections.Should().HaveCount(2, "the retry recovers a valid layout");
        caps.Should().HaveCount(2);
        caps[0].Should().NotBeNull();
        caps[1].Should().NotBeNull();
        caps[0]!.Value.Should().BeGreaterThanOrEqualTo(_config.SetupMaxTokens);
        caps[1]!.Value.Should().BeGreaterThan(caps[0]!.Value,
            "the retry must raise the output-token cap, not merely re-issue the same request");
    }

    [Fact]
    public async Task InferPatternFromAnswersAsync_PersistentTruncation_ThrowsTypedException()
    {
        // If it keeps truncating, the caller must receive the typed truncation
        // signal (so the UI reports the real cause), not a bare JsonException.
        _settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");
        var analyzer = CreateAnalyzer((_, _, _, _, _) => Task.FromResult(string.Empty));

        var act = async () => await analyzer.InferPatternFromAnswersAsync(
            null, SetupLinks(), "https://x.com/", EmptyProposal(), new List<SetupAnswer>());

        await act.Should().ThrowAsync<LayoutTruncationException>();
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
