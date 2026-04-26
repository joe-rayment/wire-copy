// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class ScoredClassifierTests
{
    #region Real-World Article Scenarios

    [Fact]
    public void ClassifyScored_VergeArticle_ReturnsArticle()
    {
        // The Verge article: og:type=article, NewsArticle, 1 <article>, article-body, h1, deep paras
        var signals = new PageSignals
        {
            OgType = "article",
            LdJsonType = "NewsArticle",
            ArticleContainerCount = 1,
            RoleArticleCount = 0,
            HasArticleBodyClass = true,
            HasH1 = true,
            DeepParagraphCount = 26,
            TimeElementCount = 14,
            HasMainElement = true,
        };

        var (classification, score) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 36,
            "https://www.theverge.com/science/915244/spacex-ipo-trillion-dollar-commercial-iss-nasa-launch");

        classification.Should().Be(PageClassification.Article);
        score.Should().BeGreaterThan(50, "strong article signals should produce a high score");
    }

    [Fact]
    public void ClassifyScored_NytArticle_ReturnsArticle()
    {
        // NYT article: og:type=article, NewsArticle, date-slug URL, article container, deep paras
        var signals = new PageSignals
        {
            OgType = "article",
            LdJsonType = "NewsArticle",
            ArticleContainerCount = 1,
            RoleArticleCount = 0,
            HasArticleBodyClass = true,
            HasH1 = true,
            DeepParagraphCount = 15,
            TimeElementCount = 2,
            HasMainElement = true,
        };

        var (classification, _) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 5,
            "https://www.nytimes.com/2024/11/15/us/politics/story-slug");

        classification.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void ClassifyScored_WikipediaArticle_ReturnsArticle()
    {
        // Wikipedia: no og:type, no ld+json, no <article>, but main + h1 + deep paragraphs
        var signals = new PageSignals
        {
            OgType = null,
            LdJsonType = null,
            ArticleContainerCount = 0,
            RoleArticleCount = 0,
            HasArticleBodyClass = false,
            HasH1 = true,
            DeepParagraphCount = 28,
            TimeElementCount = 0,
            HasMainElement = true,
        };

        var (classification, score) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 200,
            "https://en.wikipedia.org/wiki/Terminal_emulator");

        classification.Should().Be(PageClassification.Article);
        score.Should().BeGreaterOrEqualTo(25, "h1 + paragraphs + main should reach Article threshold");
    }

    [Fact]
    public void ClassifyScored_SubstackPost_ReturnsArticle()
    {
        var signals = new PageSignals
        {
            OgType = "article",
            LdJsonType = null,
            ArticleContainerCount = 1,
            HasArticleBodyClass = true,
            HasH1 = true,
            DeepParagraphCount = 8,
            HasMainElement = true,
        };

        var (classification, _) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 3,
            "https://newsletter.example.com/p/interesting-post");

        classification.Should().Be(PageClassification.Article);
    }

    #endregion

    #region Real-World LinkList Scenarios

    [Fact]
    public void ClassifyScored_VergeHomepage_ReturnsLinkList()
    {
        // The Verge homepage: og:type=website, 0 articles, 17 role=article cards
        var signals = new PageSignals
        {
            OgType = "website",
            LdJsonType = null, // NewsMediaOrganization filtered as boilerplate
            ArticleContainerCount = 0,
            RoleArticleCount = 17,
            HasArticleBodyClass = false,
            HasH1 = false,
            DeepParagraphCount = 5,
            TimeElementCount = 46,
            HasMainElement = true,
        };

        var (classification, score) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 80,
            "https://www.theverge.com/");

        classification.Should().Be(PageClassification.LinkList);
        score.Should().BeLessThan(-50, "homepage with website og:type + many cards should be strong LinkList");
    }

    [Fact]
    public void ClassifyScored_NytHomepage_ReturnsLinkList()
    {
        var signals = new PageSignals
        {
            OgType = "website",
            LdJsonType = "WebSite",
            ArticleContainerCount = 8,
            RoleArticleCount = 0,
            HasArticleBodyClass = false,
            HasH1 = false,
            DeepParagraphCount = 2,
            TimeElementCount = 30,
            HasMainElement = true,
        };

        var (classification, _) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 50,
            "https://www.nytimes.com/");

        classification.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void ClassifyScored_HackerNews_ReturnsLinkList()
    {
        // HN: no article tags, no og:type, table layout, many links
        var signals = new PageSignals
        {
            OgType = null,
            LdJsonType = null,
            ArticleContainerCount = 0,
            RoleArticleCount = 0,
            HasArticleBodyClass = false,
            HasH1 = false,
            DeepParagraphCount = 0,
            TimeElementCount = 0,
            HasMainElement = false,
        };

        var (classification, _) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 30,
            "https://news.ycombinator.com/");

        classification.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void ClassifyScored_VergeSectionIndex_ReturnsLinkList()
    {
        // /science (bare) is a section index
        var signals = new PageSignals
        {
            OgType = "website",
            ArticleContainerCount = 0,
            RoleArticleCount = 10,
            HasArticleBodyClass = false,
            HasH1 = false,
            DeepParagraphCount = 2,
            TimeElementCount = 20,
            HasMainElement = true,
        };

        var (classification, _) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 40,
            "https://www.theverge.com/science");

        classification.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ClassifyScored_BrokenCms_OgTypeArticleEverywhere_Moderate()
    {
        // Broken CMS: og:type=article on a section page with many links
        var signals = new PageSignals
        {
            OgType = "article",
            LdJsonType = null,
            ArticleContainerCount = 5,
            RoleArticleCount = 0,
            HasArticleBodyClass = false,
            HasH1 = false,
            DeepParagraphCount = 0,
            TimeElementCount = 15,
            HasMainElement = true,
        };

        var (classification, score) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 40,
            "https://example.com/news");

        // og:type=article (+30) + main (+10) - section URL (-30) - 5 article containers (-25)
        // - links penalty - time elements
        // Should NOT be Article despite og:type
        classification.Should().NotBe(PageClassification.Article,
            "broken CMS og:type should be overridden by strong LinkList signals");
    }

    [Fact]
    public void ClassifyScored_EmptySignals_Root_ReturnsLinkList()
    {
        var signals = new PageSignals();
        var (classification, _) = PageClassifier.ClassifyScored(signals, 0, "https://example.com/");
        classification.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void ClassifyScored_EmptySignals_NonRoot_ReturnsUnknown()
    {
        var signals = new PageSignals();
        var (classification, _) = PageClassifier.ClassifyScored(signals, 0, "https://example.com/page");
        classification.Should().Be(PageClassification.Unknown);
    }

    [Fact]
    public void ClassifyScored_ScoreReturned_IsNonZero()
    {
        var signals = new PageSignals { OgType = "article", HasH1 = true };
        var (_, score) = PageClassifier.ClassifyScored(signals, 0, "https://example.com/post");
        score.Should().NotBe(0, "at least og:type + h1 should produce a positive score");
    }

    [Fact]
    public void ClassifyScored_ArticleWithManyLinks_StillArticle()
    {
        // Long article with 80 inline links — link penalty should not override strong article signals
        var signals = new PageSignals
        {
            OgType = "article",
            LdJsonType = "NewsArticle",
            ArticleContainerCount = 1,
            HasArticleBodyClass = true,
            HasH1 = true,
            DeepParagraphCount = 20,
            HasMainElement = true,
        };

        var (classification, _) = PageClassifier.ClassifyScored(
            signals, contentLinkCount: 80,
            "https://example.com/deep-dive-article");

        classification.Should().Be(PageClassification.Article,
            "strong article signals should survive link penalty");
    }

    #endregion
}
