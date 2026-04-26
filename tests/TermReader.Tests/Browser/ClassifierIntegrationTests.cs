// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// End-to-end tests that exercise PageSignalExtractor + ClassifyScored together,
/// using HTML fixtures that match real-world site structures.
/// These tests prevent the "fix one site, break another" regression cycle.
/// </summary>
[Trait("Category", "Unit")]
public class ClassifierIntegrationTests
{
    /// <summary>
    /// Runs the full pipeline: extract signals from HTML, classify with URL.
    /// </summary>
    private static (PageClassification Classification, int Score) ClassifyHtml(
        string html, int contentLinkCount, string url)
    {
        var signals = PageSignalExtractor.Extract(html);
        return PageClassifier.ClassifyScored(signals, contentLinkCount, url);
    }

    #region The Verge

    [Fact]
    public void VergeArticle_FullPipeline_ClassifiesAsArticle()
    {
        // Minimal fixture matching Verge article structure
        var longPara = new string('x', 300);
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"article\">" +
            "<script type=\"application/ld+json\">{\"@type\":\"NewsArticle\"}</script>" +
            "</head><body><main><article>" +
            "<h1>SpaceX IPO</h1>" +
            "<div class=\"duet--article--article-body-component\">" +
            "<p>" + longPara + "</p><p>" + longPara + "</p><p>" + longPara + "</p>" +
            "</div></article></main></body></html>";

        var (classification, score) = ClassifyHtml(html, 36,
            "https://www.theverge.com/science/915244/spacex-ipo-trillion-dollar-commercial-iss-nasa-launch");

        classification.Should().Be(PageClassification.Article);
        score.Should().BeGreaterThan(50);
    }

    [Fact]
    public void VergeHomepage_FullPipeline_ClassifiesAsLinkList()
    {
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"website\">" +
            "</head><body><main>" +
            "<div role=\"article\">Post 1</div>" +
            "<div role=\"article\">Post 2</div>" +
            "<div role=\"article\">Post 3</div>" +
            "<div role=\"article\">Post 4</div>" +
            "</main></body></html>";

        var (classification, _) = ClassifyHtml(html, 80, "https://www.theverge.com/");
        classification.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void VergeScienceSection_FullPipeline_ClassifiesAsLinkList()
    {
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"website\">" +
            "</head><body><main>" +
            "<div role=\"article\">Post 1</div>" +
            "<div role=\"article\">Post 2</div>" +
            "<div role=\"article\">Post 3</div>" +
            "</main></body></html>";

        var (classification, _) = ClassifyHtml(html, 30, "https://www.theverge.com/science");
        classification.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region New York Times

    [Fact]
    public void NytArticle_FullPipeline_ClassifiesAsArticle()
    {
        var longPara = new string('x', 300);
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"article\">" +
            "<script type=\"application/ld+json\">{\"@type\":\"NewsArticle\"}</script>" +
            "</head><body><main>" +
            "<article data-testid=\"article-body\">" +
            "<h1>Breaking News Story</h1>" +
            "<div class=\"StoryBodyCompanionColumn\">" +
            "<p>" + longPara + "</p><p>" + longPara + "</p><p>" + longPara + "</p>" +
            "</div></article></main></body></html>";

        var (classification, _) = ClassifyHtml(html, 5,
            "https://www.nytimes.com/2024/11/15/us/politics/story-slug");
        classification.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void NytHomepage_FullPipeline_ClassifiesAsLinkList()
    {
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"website\">" +
            "<script type=\"application/ld+json\">{\"@type\":\"WebSite\"}</script>" +
            "</head><body><main>" +
            "<article>Card 1</article><article>Card 2</article><article>Card 3</article>" +
            "<article>Card 4</article><article>Card 5</article>" +
            "</main></body></html>";

        var (classification, _) = ClassifyHtml(html, 50, "https://www.nytimes.com/");
        classification.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Wikipedia

    [Fact]
    public void WikipediaArticle_FullPipeline_ClassifiesAsArticle()
    {
        // Wikipedia: no og:type, no ld+json, no <article>, but main + h1 + many paragraphs
        var longPara = new string('W', 300);
        var paras = string.Join("", Enumerable.Range(0, 15).Select(_ => "<p>" + longPara + "</p>"));
        var html = "<html><head></head><body>" +
            "<main class=\"mw-body\">" +
            "<h1>Terminal emulator</h1>" +
            "<div class=\"mw-parser-output\">" + paras + "</div>" +
            "</main></body></html>";

        var (classification, _) = ClassifyHtml(html, 200,
            "https://en.wikipedia.org/wiki/Terminal_emulator");
        classification.Should().Be(PageClassification.Article);
    }

    #endregion

    #region Hacker News

    [Fact]
    public void HackerNews_FullPipeline_ClassifiesAsLinkList()
    {
        // HN: minimal HTML, no semantic markup, many links
        var html = "<html><head></head><body>" +
            "<table><tr><td><a>Story 1</a></td></tr></table>" +
            "</body></html>";

        var (classification, _) = ClassifyHtml(html, 30, "https://news.ycombinator.com/");
        classification.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Substack

    [Fact]
    public void SubstackPost_FullPipeline_ClassifiesAsArticle()
    {
        var longPara = new string('S', 300);
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"article\">" +
            "</head><body><main>" +
            "<article><h1>Newsletter Issue #42</h1>" +
            "<div class=\"available-content post-full-content\">" +
            "<p>" + longPara + "</p><p>" + longPara + "</p><p>" + longPara + "</p>" +
            "</div></article></main></body></html>";

        var (classification, _) = ClassifyHtml(html, 3,
            "https://newsletter.example.com/p/interesting-post");
        classification.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void SubstackHomepage_FullPipeline_ClassifiesAsLinkList()
    {
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"website\">" +
            "</head><body><main>" +
            "<article>Post preview 1</article>" +
            "<article>Post preview 2</article>" +
            "<article>Post preview 3</article>" +
            "</main></body></html>";

        var (classification, _) = ClassifyHtml(html, 20,
            "https://newsletter.example.com/");
        classification.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Cross-Site URL Pattern Tests

    [Theory]
    [InlineData("https://www.theverge.com/science/915244/spacex-ipo", false)]
    [InlineData("https://www.theverge.com/science", true)]
    [InlineData("https://www.nytimes.com/section/opinion", true)]
    [InlineData("https://www.nytimes.com/2024/01/15/us/story", false)]
    [InlineData("https://arstechnica.com/science/2024/01/story", false)]
    [InlineData("https://arstechnica.com/science", true)]
    [InlineData("https://example.com/", true)]
    [InlineData("https://example.com/technology/12345/article", false)]
    public void IsSectionUrlPattern_CrossSite_ConsistentBehavior(string url, bool expectedSection)
    {
        PageClassifier.IsSectionUrlPattern(url).Should().Be(expectedSection,
            $"URL '{url}' section detection should be consistent across sites");
    }

    #endregion
}
