// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class SelectorBasedArticleExtractorTests
{
    private readonly SelectorBasedArticleExtractor _sut;

    public SelectorBasedArticleExtractorTests()
    {
        var logger = Substitute.For<ILogger<SelectorBasedArticleExtractor>>();
        _sut = new SelectorBasedArticleExtractor(logger);
    }

    private static string MakeArticleHtml(string headline = "An Important Test Headline About Civic Affairs")
    {
        // Build paragraphs deliberately long enough (and with varied first words)
        // to clear ReadableContentExtractor.ValidateContentQuality.
        var paras = new[]
        {
            "The reporters spent several weeks investigating the policy and its consequences for the city's infrastructure, traffic patterns, and emergency services across all five boroughs.",
            "Officials acknowledged the pressure on response times and said funding allocations from the upcoming budget would address the shortfall, though some councilmembers questioned the timeline.",
            "Critics warned that without independent oversight the project would repeat the procurement failures of the previous decade, citing the contracting scandals reported earlier this year.",
            "Community groups in the affected districts have begun organizing public meetings, where residents are expected to demand transparent reporting and quarterly progress updates from the agency.",
            "Independent analysts contacted by this paper described the proposal as ambitious but achievable, provided the agency hires enough engineers and avoids the staffing churn it has experienced.",
            "Meanwhile, transit advocates pointed to similar programs in peer cities, where coordinated planning between transit, utilities, and emergency services yielded measurable improvements within two years.",
            "Several elected officials said they would push for a sunset clause that requires renewed legislative approval after three years, ensuring future councils retain meaningful oversight.",
            "Residents interviewed near the proposed work zone offered mixed reactions, balancing skepticism shaped by past disruptions with cautious hope that long-promised upgrades will finally arrive.",
        };

        var bodyHtml = string.Concat(paras.Select(p => $"<p>{p}</p>"));

        return $$"""
            <html>
              <head>
                <meta property="og:type" content="article" />
                <script type="application/ld+json">{"@type":"NewsArticle","headline":"{{headline}}"}</script>
              </head>
              <body class="page-news">
                <header><nav>Top nav</nav></header>
                <article id="story">
                  <h1 class="headline">{{headline}}</h1>
                  <span class="byline">By Jane Reporter</span>
                  <time datetime="2026-05-09T12:00:00Z">May 9, 2026</time>
                  <div class="ad">SPONSORED CONTENT</div>
                  {{bodyHtml}}
                </article>
              </body>
            </html>
            """;
    }

    private static ArticleSelectorConfig MakeConfig(params PageTypeEntry[] entries)
    {
        return new ArticleSelectorConfig
        {
            Domain = "example.com",
            PageTypes = entries.ToList(),
        };
    }

    private static PageTypeEntry MakeArticleEntry(int priority = 10, string name = "article")
    {
        return new PageTypeEntry
        {
            Name = name,
            PageType = PageType.Article,
            Priority = priority,
            Matcher = new PageTypeMatcher
            {
                UrlPattern = "example\\.com",
            },
            Selectors = new ArticleSelectors
            {
                Headline = new List<string> { "//h1[contains(@class,'headline')]" },
                Byline = new List<string> { "//span[contains(@class,'byline')]" },
                PublishDate = new List<string> { "//time[@datetime]" },
                Body = new List<string> { "//article[@id='story']" },
                ExcludeRegions = new List<string> { "//div[contains(@class,'ad')]" },
            },
            Quality = new ArticleQualityThresholds { MinWords = 100, MinParagraphs = 3 },
        };
    }

    [Fact]
    public void Extract_MatchingEntry_ReturnsContent()
    {
        var html = MakeArticleHtml();
        var config = MakeConfig(MakeArticleEntry());

        var result = _sut.Extract(config, "https://example.com/story", html);

        result.Should().NotBeNull();
        result!.Title.Should().Be("An Important Test Headline About Civic Affairs");
        result.Author.Should().Be("By Jane Reporter");
        result.Paragraphs.Should().HaveCountGreaterOrEqualTo(8);
        result.WordCount.Should().BeGreaterThan(100);
        result.PublishedDate.Should().NotBeNull();
    }

    [Fact]
    public void Extract_ExcludeRegions_RemovesAdContent()
    {
        var html = MakeArticleHtml();
        var config = MakeConfig(MakeArticleEntry());

        var result = _sut.Extract(config, "https://example.com/story", html);

        result.Should().NotBeNull();
        // The "SPONSORED CONTENT" div is in ExcludeRegions and should not appear in body paragraphs.
        result!.CleanedText.Should().NotContain("SPONSORED CONTENT");
    }

    [Fact]
    public void PickEntry_HighestPriorityWins()
    {
        var html = MakeArticleHtml();
        var generic = MakeArticleEntry(priority: 10, name: "generic");
        var liveBlog = MakeArticleEntry(priority: 100, name: "live-blog");
        var config = MakeConfig(generic, liveBlog);

        var picked = _sut.PickEntry(config, "https://example.com/story", html);

        picked.Should().NotBeNull();
        picked!.Name.Should().Be("live-blog");
    }

    [Fact]
    public void PickEntry_TiesResolvedByDeclarationOrder()
    {
        var html = MakeArticleHtml();
        var first = MakeArticleEntry(priority: 50, name: "first");
        var second = MakeArticleEntry(priority: 50, name: "second");
        var config = MakeConfig(first, second);

        var picked = _sut.PickEntry(config, "https://example.com/story", html);

        picked.Should().NotBeNull();
        picked!.Name.Should().Be("first");
    }

    [Fact]
    public void PickEntry_NoMatcherMatch_ReturnsNull()
    {
        var html = MakeArticleHtml();
        var entry = MakeArticleEntry() with
        {
            Matcher = new PageTypeMatcher { UrlPattern = "another-site\\.com" },
        };
        var config = MakeConfig(entry);

        var picked = _sut.PickEntry(config, "https://example.com/story", html);

        picked.Should().BeNull();
    }

    [Fact]
    public void Extract_BelowMinParagraphs_ReturnsNull()
    {
        var html = MakeArticleHtml();
        var entry = MakeArticleEntry();
        entry = entry with
        {
            Quality = new ArticleQualityThresholds { MinWords = 100, MinParagraphs = 999 },
        };
        var config = MakeConfig(entry);

        var result = _sut.Extract(config, "https://example.com/story", html);

        result.Should().BeNull("the entry's MinParagraphs threshold cannot be met");
    }

    [Fact]
    public void PickEntry_LdJsonTypeMatcher_RequiresMatchingType()
    {
        var html = MakeArticleHtml();
        var matchingEntry = MakeArticleEntry() with
        {
            Matcher = new PageTypeMatcher { LdJsonType = "NewsArticle" },
        };
        var nonMatchingEntry = MakeArticleEntry(name: "live-blog") with
        {
            Matcher = new PageTypeMatcher { LdJsonType = "LiveBlogPosting" },
            Priority = 100,
        };
        var config = MakeConfig(nonMatchingEntry, matchingEntry);

        var picked = _sut.PickEntry(config, "https://example.com/story", html);

        // Live-blog matcher fails (the page advertises NewsArticle), so the
        // generic article entry wins despite the lower priority.
        picked.Should().NotBeNull();
        picked!.Name.Should().Be("article");
    }

    [Fact]
    public void PickEntry_BodyClassMatcher_RequiresContains()
    {
        var html = MakeArticleHtml();
        var entry = MakeArticleEntry() with
        {
            Matcher = new PageTypeMatcher { BodyClassContains = "page-news" },
        };
        var miss = MakeArticleEntry(name: "miss") with
        {
            Matcher = new PageTypeMatcher { BodyClassContains = "page-recipe" },
        };
        var config = MakeConfig(entry, miss);

        var picked = _sut.PickEntry(config, "https://example.com/story", html);

        picked.Should().NotBeNull();
        picked!.Name.Should().Be("article");
    }

    [Fact]
    public void PickEntry_EmptyConfig_ReturnsNull()
    {
        var picked = _sut.PickEntry(MakeConfig(), "https://example.com/story", "<html/>");

        picked.Should().BeNull();
    }

    [Fact]
    public void Extract_GarbledHtml_ReturnsNullGracefully()
    {
        var config = MakeConfig(MakeArticleEntry());

        var result = _sut.Extract(config, "https://example.com/story", "<not-html>");

        result.Should().BeNull();
    }
}
