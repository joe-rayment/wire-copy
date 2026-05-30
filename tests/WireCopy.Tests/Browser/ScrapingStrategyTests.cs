// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.ScrapingStrategies;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ScrapingStrategyTests
{
    private static List<LinkInfo> BuildTechmemeFixture()
    {
        // 8 ads + 30 stories — names mimic real techmeme ad copy.
        var links = new List<LinkInfo>();
        for (int i = 0; i < 8; i++)
        {
            links.Add(new LinkInfo
            {
                Url = $"https://example.com/ad{i}",
                DisplayText = $"Sponsored: deal {i}",
                Type = LinkType.Content,
                ImportanceScore = 50,
            });
        }

        for (int i = 0; i < 30; i++)
        {
            links.Add(new LinkInfo
            {
                Url = $"https://example.com/story{i}",
                DisplayText = $"Story {i}",
                Type = LinkType.Content,
                ImportanceScore = 80,
            });
        }

        return links;
    }

    [Fact]
    public async Task NavigationTreeBuilder_BuildFromAiResult_ExcludedLinksAbsent()
    {
        var logger = Substitute.For<ILogger<NavigationTreeBuilder>>();
        var builder = new NavigationTreeBuilder(logger);

        var links = BuildTechmemeFixture();
        var excludedKeys = links
            .Take(8)
            .Select(l => AiCuratedResult.KeyFor(l.Url))
            .ToList();
        var storyKeys = links
            .Skip(8)
            .Select(l => AiCuratedResult.KeyFor(l.Url))
            .ToList();

        var curated = new AiCuratedResult
        {
            ExcludedLinkKeys = excludedKeys,
            StoryOrderLinkKeys = storyKeys,
            AnalyzedAt = DateTime.UtcNow,
        };

        var tree = await builder.BuildFromAiResultAsync(links, curated);

        tree.TotalLinks.Should().Be(30);
        var visible = tree.GetVisibleNodes().ToList();
        visible.Should().NotContain(n => n.Link.Url.Contains("/ad", StringComparison.Ordinal));
        visible.Where(n => n.Link.Url.Contains("/story", StringComparison.Ordinal))
            .Should().HaveCount(30);
    }

    [Fact]
    public async Task AiCuratedStrategy_FiltersExcludedLinks()
    {
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var links = BuildTechmemeFixture();
        var excludedKeys = links.Take(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();
        var storyKeys = links.Skip(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCuratedResult
            {
                ExcludedLinkKeys = excludedKeys,
                StoryOrderLinkKeys = storyKeys,
                AnalyzedAt = DateTime.UtcNow,
            });

        var treeBuilder = new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>());
        var strategy = new AiCuratedStrategy(
            treeBuilder,
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var ctx = new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = links,
        };

        var result = await strategy.BuildTreeAsync(ctx);

        result.Tree.TotalLinks.Should().Be(30);
        result.Config.Strategy.Should().Be("AiCurated");
        result.Config.AiResult.Should().NotBeNull();
        result.Config.AiResult!.ExcludedLinkKeys.Should().HaveCount(8);
    }

    // workspace-5oe9.5: a fixture WHOSE LINKS CARRY STRUCTURE (parent selectors)
    // so the strategy can derive a durable, generalizing pattern config.
    private static List<LinkInfo> BuildStructuredFixture(string datePath)
    {
        LinkInfo L(string slug, string text, int score, string parent) => new()
        {
            Url = $"https://news.example.com/{datePath}/{slug}",
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = score,
            ParentSelector = parent,
        };

        // Document order deliberately does NOT match editorial prominence (the
        // lead sits mid-document), so a by-prominence ranking is non-degenerate.
        return new List<LinkInfo>
        {
            L("feed-a", "Feed A", 70, "main section.feed li a"),
            L("feed-b", "Feed B", 65, "main section.feed li a"),
            L("lead", "Lead story", 95, "main section.lead > h1 > a"),
            L("feed-c", "Feed C", 60, "main section.feed li a"),
            L("promo-1", "Subscribe now", 30, "aside.promo a"),
            L("promo-2", "Download the app", 20, "aside.promo a"),
        };
    }

    private static IHierarchyAnalyzer FakeAnalyzerFor(List<LinkInfo> links, int adCount)
    {
        var excluded = links.TakeLast(adCount).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();
        // Rank stories by prominence (descending score) so the lead is #1 and the
        // ranking differs from document order.
        var stories = links.Take(links.Count - adCount)
            .OrderByDescending(l => l.ImportanceScore)
            .Select(l => AiCuratedResult.KeyFor(l.Url))
            .ToList();
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCuratedResult
            {
                ExcludedLinkKeys = excluded,
                StoryOrderLinkKeys = stories,
                AnalyzedAt = DateTime.UtcNow,
            });
        return analyzer;
    }

    private static AiCuratedStrategy NewStrategy(IHierarchyAnalyzer analyzer, NavigationTreeBuilder? builder = null) =>
        new(
            builder ?? new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

    [Fact]
    public async Task AiCuratedStrategy_StructuredPage_EmitsDurablePatternConfig()
    {
        var links = BuildStructuredFixture("2026/05/30");
        var strategy = NewStrategy(FakeAnalyzerFor(links, adCount: 2));
        var ctx = new ScrapingStrategyContext { PageUrl = "https://news.example.com/", Html = string.Empty, Links = links };

        var result = await strategy.BuildTreeAsync(ctx);

        result.Config.Version.Should().Be(3);
        result.Config.Strategy.Should().Be("AiCurated");
        result.Config.Sections.Should().NotBeEmpty();
        // Durable identifiers, NOT per-URL "url:" snapshot keys.
        var allSelectors = result.Config.Sections.SelectMany(s => s.ParentSelectors).ToList();
        allSelectors.Should().Contain(s => s.Contains("section.lead", StringComparison.Ordinal));
        allSelectors.Should().Contain(s => s.Contains("section.feed", StringComparison.Ordinal));
        allSelectors.Should().NotContain(s => s.StartsWith("url:", StringComparison.Ordinal));
        result.Config.ExcludeSelectors.Should().Contain("aside.promo");
        result.Config.NeedsReanalyze.Should().BeFalse();

        // First visit renders the PATTERN tree (has a "Top Story" section header),
        // not the flat AiResult snapshot.
        result.Tree.GetAllNodes().Should().Contain(n => n.IsGroupHeader && n.Link.DisplayText == "Top Story");
    }

    [Fact]
    public async Task AiCuratedStrategy_DurableConfig_SurvivesDisjointRevisit()
    {
        // Build the config while looking at snapshot A...
        var snapshotA = BuildStructuredFixture("2026/05/30");
        var strategy = NewStrategy(FakeAnalyzerFor(snapshotA, adCount: 2));
        var config = (await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://news.example.com/", Html = string.Empty, Links = snapshotA,
        })).Config;
        config.Sections.Should().NotBeEmpty();

        // ...then apply it to snapshot B with ENTIRELY different URLs.
        var snapshotB = BuildStructuredFixture("2026/06/15");
        snapshotA.Select(l => l.Url).Should().NotIntersectWith(snapshotB.Select(l => l.Url));

        var builder = new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>());
        var tree = await builder.BuildTreeAsync(snapshotB, config);

        var lead = tree.Root.Children.First(n => n.IsGroupHeader && n.Link.DisplayText == "Top Story");
        lead.Children.Should().ContainSingle()
            .Which.Link.Url.Should().Be("https://news.example.com/2026/06/15/lead");
        // The promos are excluded on the new snapshot too (durable rule).
        tree.GetAllNodes().Select(n => n.Link.Url)
            .Should().NotContain(u => u.Contains("/promo-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AiCuratedStrategy_UnstructuredPage_FallsBackToSnapshotAndFlagsReanalyze()
    {
        // No parent selectors + unique slugs => no durable identifier can be
        // derived => self-test fails => snapshot fallback + NeedsReanalyze.
        var links = BuildTechmemeFixture();
        var strategy = NewStrategy(FakeAnalyzerFor(links, adCount: 8));
        var ctx = new ScrapingStrategyContext { PageUrl = "https://techmeme.com/", Html = string.Empty, Links = links };

        var result = await strategy.BuildTreeAsync(ctx);

        result.Config.Sections.Should().BeEmpty();
        result.Config.NeedsReanalyze.Should().BeTrue();
        result.Config.AiResult.Should().NotBeNull("the snapshot is kept as the in-session fallback");
        result.Tree.TotalLinks.Should().Be(30, "the snapshot path still renders the 30 stories this session");
    }

    [Fact]
    public async Task AiCuratedStrategy_OrdersByPrompted()
    {
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiApiKey").Returns("sk-test-key");

        var links = BuildTechmemeFixture();

        // Reverse the story order: story29 first, story0 last.
        var stories = links.Skip(8).ToList();
        var reversedKeys = stories.AsEnumerable().Reverse().Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCuratedResult
            {
                ExcludedLinkKeys = links.Take(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList(),
                StoryOrderLinkKeys = reversedKeys,
                AnalyzedAt = DateTime.UtcNow,
            });

        var treeBuilder = new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>());
        var strategy = new AiCuratedStrategy(
            treeBuilder,
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var result = await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = links,
        });

        var visible = result.Tree.GetVisibleNodes()
            .Where(n => n.Link.Url.Contains("/story", StringComparison.Ordinal))
            .ToList();
        visible.Should().HaveCount(30);
        visible[0].Link.Url.Should().Be("https://example.com/story29");
        visible[^1].Link.Url.Should().Be("https://example.com/story0");
    }

    /// <summary>
    /// workspace-hapr: regression pin — when the AI returns a ranking that
    /// EXACTLY matches the surviving document order (which is what we suspect
    /// is happening on macleans.ca), the strategy surfaces "no reordering" in
    /// the summary so the user knows AI Curated added no editorial value.
    /// Without this guard, the summary reads "AI curated · N stories" — the
    /// same as if the AI had actually reordered, and the user sees doc order
    /// silently.
    /// </summary>
    [Fact]
    public async Task AiCuratedStrategy_MatchesDocumentOrder_SummaryFlagsNoReordering()
    {
        var links = BuildTechmemeFixture();
        var excludedKeys = links.Take(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        // Critical: ranked keys are in the SAME order as the surviving doc
        // order (story0, story1, ..., story29). This is the macleans.ca-like
        // failure mode.
        var degenerateRanking = links.Skip(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCuratedResult
            {
                ExcludedLinkKeys = excludedKeys,
                StoryOrderLinkKeys = degenerateRanking,
                AnalyzedAt = DateTime.UtcNow,
            });

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var result = await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://macleans.ca/",
            Html = string.Empty,
            Links = links,
        });

        result.Summary.Should().Contain("no reordering",
            "the user MUST see that the AI's ranking matched document order — otherwise the bug shape (workspace-hapr) is invisible");
        result.Summary.Should().Contain("document order");
    }

    [Fact]
    public async Task AiCuratedStrategy_EmptyRanking_SummaryFlagsEmpty()
    {
        var links = BuildTechmemeFixture();
        var allExcluded = links.Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCuratedResult
            {
                ExcludedLinkKeys = allExcluded,
                StoryOrderLinkKeys = new List<string>(),
                AnalyzedAt = DateTime.UtcNow,
            });

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var result = await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://macleans.ca/",
            Html = string.Empty,
            Links = links,
        });

        result.Summary.Should().Contain("empty result",
            "an empty StoryOrderLinkKeys must be surfaced as a degenerate result, not as 'AI curated · 0 stories' that looks like a normal small page");
    }

    [Fact]
    public async Task AiCuratedStrategy_GenuineReorder_NoDegenerateFlag()
    {
        var links = BuildTechmemeFixture();
        var stories = links.Skip(8).ToList();
        var reversedKeys = stories.AsEnumerable().Reverse().Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCuratedResult
            {
                ExcludedLinkKeys = links.Take(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList(),
                StoryOrderLinkKeys = reversedKeys,
                AnalyzedAt = DateTime.UtcNow,
            });

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var result = await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = links,
        });

        result.Summary.Should().NotContain("no reordering");
        result.Summary.Should().NotContain("empty result");
        result.Summary.Should().Contain("30 stories");
    }

    [Fact]
    public async Task AiCuratedStrategy_NoApiKey_StrategyUnavailable()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(false);

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var availability = await strategy.IsAvailableAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = BuildTechmemeFixture(),
        });

        availability.IsAvailable.Should().BeFalse();
        availability.ReasonWhenUnavailable.Should().Contain("API key");
    }

    [Fact]
    public async Task RssFeedStrategy_PicksAdvertisedFeed()
    {
        var html = "<html><head><link rel=\"alternate\" type=\"application/rss+xml\" href=\"https://example.com/feed.xml\" title=\"Feed\"/></head></html>";

        var rssXml = "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>X</title>" +
                     "<item><title>Item 1</title><link>https://example.com/i1</link></item>" +
                     "<item><title>Item 2</title><link>https://example.com/i2</link></item>" +
                     "</channel></rss>";

        var handler = new RoutingHttpHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://example.com/feed.xml")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(rssXml, System.Text.Encoding.UTF8, "application/rss+xml"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var httpClient = new HttpClient(handler);

        var detector = new RssFeedDetector(Substitute.For<ILogger<RssFeedDetector>>(), httpClient);
        var strategy = new RssFeedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            detector,
            Substitute.For<ILogger<RssFeedStrategy>>());

        var ctx = new ScrapingStrategyContext
        {
            PageUrl = "https://example.com/",
            Html = html,
            Links = new List<LinkInfo>(),
        };

        var availability = await strategy.IsAvailableAsync(ctx);
        availability.IsAvailable.Should().BeTrue();
        availability.StatusDetail.Should().Contain("feed.xml");

        var result = await strategy.BuildTreeAsync(ctx);
        result.Config.RssFeedUrl.Should().Be("https://example.com/feed.xml");
        result.Tree.TotalLinks.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RssFeedStrategy_FallsBackToWellKnown()
    {
        var rssXml = "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>X</title>" +
                     "<item><title>Item 1</title><link>https://example.com/i1</link></item>" +
                     "</channel></rss>";

        var handler = new RoutingHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head && req.RequestUri!.AbsoluteUri == "https://example.com/feed/")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty),
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/rss+xml");
                return response;
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri == "https://example.com/feed/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(rssXml, System.Text.Encoding.UTF8, "application/rss+xml"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var httpClient = new HttpClient(handler);

        var detector = new RssFeedDetector(Substitute.For<ILogger<RssFeedDetector>>(), httpClient);
        var strategy = new RssFeedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            detector,
            Substitute.For<ILogger<RssFeedStrategy>>());

        var availability = await strategy.IsAvailableAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://example.com/",
            Html = "<html></html>",  // no advertised feed
            Links = new List<LinkInfo>(),
        });

        availability.IsAvailable.Should().BeTrue();
        availability.StatusDetail.Should().Contain("/feed/");
    }

    [Fact]
    public async Task HierarchyConfigStore_TechmemeChoiceDoesNotAffectNytConfig()
    {
        var logger = Substitute.For<ILogger<HierarchyConfigStore>>();
        var store = new HierarchyConfigStore(logger);

        var techmemeConfig = new SiteHierarchyConfig
        {
            Domain = "tm-test-domain.example",
            UrlPattern = "^https?://tm-test-domain\\.example/?",
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "ai-curated",
            Kind = LayoutKind.AiCurated,
            Version = 2,
            Strategy = "AiCurated",
            AiResult = new AiCuratedResult
            {
                ExcludedLinkKeys = new List<string> { "url:https://x" },
                StoryOrderLinkKeys = new List<string>(),
                AnalyzedAt = DateTime.UtcNow,
            },
        };

        var nytConfig = new SiteHierarchyConfig
        {
            Domain = "nyt-test-domain.example",
            UrlPattern = "^https?://nyt-test-domain\\.example/?",
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "rss-feed",
            Kind = LayoutKind.RssFeed,
            Version = 2,
            Strategy = "RssFeed",
            RssFeedUrl = "https://nyt-test-domain.example/feed.xml",
        };

        try
        {
            await store.SaveConfigAsync(techmemeConfig);
            await store.SaveConfigAsync(nytConfig);

            var fetchedTm = await store.GetConfigAsync("https://tm-test-domain.example/");
            var fetchedNyt = await store.GetConfigAsync("https://nyt-test-domain.example/");

            fetchedTm.Should().NotBeNull();
            fetchedTm!.Strategy.Should().Be("AiCurated");
            fetchedNyt.Should().NotBeNull();
            fetchedNyt!.Strategy.Should().Be("RssFeed");
            fetchedNyt.RssFeedUrl.Should().Be("https://nyt-test-domain.example/feed.xml");
        }
        finally
        {
            await store.DeleteConfigAsync("https://tm-test-domain.example/");
            await store.DeleteConfigAsync("https://nyt-test-domain.example/");
        }
    }

    [Fact]
    public async Task AiCuratedStrategy_AppliesCachedResultWithoutCallingApi()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<AiCuratedResult>(_ => throw new InvalidOperationException("AI must NOT be called when cached result is fresh"));

        var links = BuildTechmemeFixture();
        var excluded = links.Take(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();
        var stories = links.Skip(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var savedConfig = new SiteHierarchyConfig
        {
            Domain = "techmeme.com",
            UrlPattern = "^https?://techmeme\\.com/?",
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "ai-curated",
            Kind = LayoutKind.AiCurated,
            Version = 2,
            Strategy = "AiCurated",
            AiResult = new AiCuratedResult
            {
                ExcludedLinkKeys = excluded,
                StoryOrderLinkKeys = stories,
                AnalyzedAt = DateTime.UtcNow.AddDays(-3),  // fresh
            },
        };

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var result = await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = links,
            SavedConfig = savedConfig,
        });

        result.Tree.TotalLinks.Should().Be(30);
        await analyzer.DidNotReceive().AnalyzeCuratedAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AiCuratedStrategy_CachedResultExpired_RunsAnalyzerAgain()
    {
        var freshResult = new AiCuratedResult
        {
            ExcludedLinkKeys = new List<string>(),
            StoryOrderLinkKeys = new List<string>(),
            AnalyzedAt = DateTime.UtcNow,
        };

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(freshResult);

        var savedConfig = new SiteHierarchyConfig
        {
            Domain = "techmeme.com",
            UrlPattern = "^https?://techmeme\\.com/?",
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "ai-curated",
            Kind = LayoutKind.AiCurated,
            Version = 2,
            Strategy = "AiCurated",
            AiResult = new AiCuratedResult
            {
                ExcludedLinkKeys = new List<string>(),
                StoryOrderLinkKeys = new List<string>(),
                AnalyzedAt = DateTime.UtcNow.AddDays(-60),  // older than default 30d TTL
            },
        };

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration { AiCuratedCacheDays = 30 }),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = BuildTechmemeFixture(),
            SavedConfig = savedConfig,
        });

        await analyzer.Received(1).AnalyzeCuratedAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AiCuratedStrategy_GuidanceChange_InvalidatesCacheAndPassesGuidance()
    {
        // workspace-99ve: cached result with no guidance must be re-analyzed
        // when the user provides guidance; the new guidance is forwarded to
        // the analyzer and stamped on the resulting AiCuratedResult.
        var links = BuildTechmemeFixture();

        var freshResult = new AiCuratedResult
        {
            ExcludedLinkKeys = new List<string>(),
            StoryOrderLinkKeys = links.Select(l => AiCuratedResult.KeyFor(l.Url)).ToList(),
            AnalyzedAt = DateTime.UtcNow,
        };

        string? capturedGuidance = null;
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(
                Arg.Any<byte[]?>(),
                Arg.Any<List<LinkInfo>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedGuidance = call.ArgAt<string?>(3);
                return Task.FromResult(freshResult);
            });

        var savedConfig = new SiteHierarchyConfig
        {
            Domain = "techmeme.com",
            UrlPattern = "^https?://techmeme\\.com/?",
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "ai-curated",
            Kind = LayoutKind.AiCurated,
            Version = 2,
            Strategy = "AiCurated",
            AiResult = new AiCuratedResult
            {
                ExcludedLinkKeys = new List<string>(),
                StoryOrderLinkKeys = links.Select(l => AiCuratedResult.KeyFor(l.Url)).ToList(),
                AnalyzedAt = DateTime.UtcNow,
                UserGuidance = null,  // previously cached without guidance
            },
        };

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var result = await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = links,
            SavedConfig = savedConfig,
            UserGuidance = "exclude opinion pieces",
        });

        capturedGuidance.Should().Be("exclude opinion pieces",
            "the analyzer must receive the user's guidance as the new fourth parameter");
        result.Config.AiResult.Should().NotBeNull();
        result.Config.AiResult!.UserGuidance.Should().Be("exclude opinion pieces",
            "guidance is stamped onto the result so the next cache match can compare against it");
    }

    [Fact]
    public async Task AiCuratedStrategy_SameGuidance_HitsCache()
    {
        // workspace-99ve: when the cached result's guidance matches the
        // request, no re-analysis is triggered.
        var links = BuildTechmemeFixture();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(
                Arg.Any<byte[]?>(),
                Arg.Any<List<LinkInfo>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns<AiCuratedResult>(_ => throw new InvalidOperationException(
                "AI must NOT be called when cached guidance matches"));

        var savedConfig = new SiteHierarchyConfig
        {
            Domain = "techmeme.com",
            UrlPattern = "^https?://techmeme\\.com/?",
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "ai-curated",
            Kind = LayoutKind.AiCurated,
            Version = 2,
            Strategy = "AiCurated",
            AiResult = new AiCuratedResult
            {
                ExcludedLinkKeys = new List<string>(),
                StoryOrderLinkKeys = links.Select(l => AiCuratedResult.KeyFor(l.Url)).ToList(),
                AnalyzedAt = DateTime.UtcNow.AddDays(-1),
                UserGuidance = "put covid first",
            },
        };

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new OpenAiHierarchyConfiguration()),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        var result = await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = links,
            SavedConfig = savedConfig,
            UserGuidance = "put covid first",
        });

        result.Config.AiResult!.UserGuidance.Should().Be("put covid first");
        await analyzer.DidNotReceive().AnalyzeCuratedAsync(
            Arg.Any<byte[]?>(),
            Arg.Any<List<LinkInfo>>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DocumentOrderStrategy_AlwaysAvailable()
    {
        var strategy = new DocumentOrderStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            Substitute.For<ILogger<DocumentOrderStrategy>>());

        var availability = await strategy.IsAvailableAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://example.com/",
            Html = string.Empty,
            Links = new List<LinkInfo>(),
        });
        availability.IsAvailable.Should().BeTrue();
    }

    private sealed class RoutingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;

        public RoutingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        {
            _route = route;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_route(request));
        }
    }
}
