// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-8qyo: the article tuner's candidate discovery and — critically —
/// the truncation guard: an ignored region must be REMOVED, never allowed to
/// end the article early.
/// </summary>
public class ArticleTunerTests
{
    private const string ArticleHtml = """
        <!DOCTYPE html><html><head><title>T</title></head><body>
          <header><h1 class="headline">Big News Headline</h1></header>
          <article class="story-body">
            <p>First paragraph of the story with plenty of words to count here. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
            <p>Second paragraph keeps the narrative going with more detail. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
            <div class="related-promo"><h3>Related coverage</h3><a href="/x">Other story</a></div>
            <p>Third paragraph AFTER the promo continues the article body text. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
            <p>Fourth paragraph closes out the story with a final thought. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
          </article>
          <aside class="newsletter">Sign up for our newsletter</aside>
        </body></html>
        """;

    [Fact]
    public void ExcludedRegion_NeverTruncatesTheArticle()
    {
        var config = new ArticleSelectorConfig
        {
            Domain = "example.com",
            PageTypes =
            [
                new PageTypeEntry
                {
                    Name = "tuned",
                    Priority = 80,
                    Selectors = new ArticleSelectors
                    {
                        Headline = ["//h1[contains(@class,'headline')]"],
                        Body = ["//article[contains(@class,'story-body')]"],
                        ExcludeRegions = ["//*[contains(@class,'related-promo')]"],
                    },
                },
            ],
        };

        var extractor = new SelectorBasedArticleExtractor(NullLogger<SelectorBasedArticleExtractor>.Instance);
        var content = extractor.Extract(config, "https://example.com/story", ArticleHtml);

        content.Should().NotBeNull();
        content!.Title.Should().Be("Big News Headline");
        var text = string.Join("\n", content.Paragraphs);
        text.Should().Contain("Third paragraph AFTER the promo",
            "the ignored promo must be removed, NOT treated as the end of the article");
        text.Should().Contain("Fourth paragraph closes out");
        text.Should().NotContain("Related coverage", "the ignored region is gone");
    }

    [Fact]
    public void BuildCandidates_ReturnsOnlyMatchingProbes_AiSeedFirst()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ArticleHtml);

        var candidates = ArticleTunerHandler.BuildCandidates(
            doc,
            aiSeed: ["//h1[contains(@class,'headline')]"],
            probes: ["//h1", "//h2", "//*[@itemprop='headline']"],
            minMatches: 1,
            maxMatches: 5);

        candidates.Should().NotBeEmpty();
        candidates[0].XPath.Should().Be("//h1[contains(@class,'headline')]", "the AI seed leads");
        candidates.Select(c => c.XPath).Should().Contain("//h1");
        candidates.Select(c => c.XPath).Should().NotContain("//h2", "nothing matches it");
    }

    [Fact]
    public void BuildBodyCandidates_RanksByParagraphDensity()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ArticleHtml);

        var candidates = ArticleTunerHandler.BuildBodyCandidates(doc, aiSeed: null);

        candidates.Should().NotBeEmpty();
        candidates[0].XPath.Should().Be("//article",
            "the article element holds the densest paragraph cluster");
    }
}
