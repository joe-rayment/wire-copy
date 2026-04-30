// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.ScrapingStrategies;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

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
        settingsStore.Get("AnthropicApiKey").Returns("sk-ant-test-key");

        var links = BuildTechmemeFixture();
        var excludedKeys = links.Take(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();
        var storyKeys = links.Skip(8).Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Options.Create(new AnthropicConfiguration()),
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

    [Fact]
    public async Task AiCuratedStrategy_OrdersByPrompted()
    {
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("AnthropicApiKey").Returns("sk-ant-test-key");

        var links = BuildTechmemeFixture();

        // Reverse the story order: story29 first, story0 last.
        var stories = links.Skip(8).ToList();
        var reversedKeys = stories.AsEnumerable().Reverse().Select(l => AiCuratedResult.KeyFor(l.Url)).ToList();

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(true);
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Options.Create(new AnthropicConfiguration()),
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

    [Fact]
    public async Task AiCuratedStrategy_NoApiKey_StrategyUnavailable()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.IsConfigured.Returns(false);

        var strategy = new AiCuratedStrategy(
            new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>()),
            analyzer,
            Options.Create(new AnthropicConfiguration()),
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
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Options.Create(new AnthropicConfiguration()),
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
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        analyzer.AnalyzeCuratedAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            Options.Create(new AnthropicConfiguration { AiCuratedCacheDays = 30 }),
            Substitute.For<ILogger<AiCuratedStrategy>>());

        await strategy.BuildTreeAsync(new ScrapingStrategyContext
        {
            PageUrl = "https://techmeme.com/",
            Html = string.Empty,
            Links = BuildTechmemeFixture(),
            SavedConfig = savedConfig,
        });

        await analyzer.Received(1).AnalyzeCuratedAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
