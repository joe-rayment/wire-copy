// Educational and personal use only.

using FluentAssertions;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class PageClassifierTests
{
    private static List<LinkInfo> CreateLinks(int contentCount, int navCount = 0)
    {
        var links = new List<LinkInfo>();
        for (var i = 0; i < contentCount; i++)
        {
            links.Add(new LinkInfo
            {
                Url = $"https://example.com/article{i}",
                DisplayText = $"Article {i}",
                Type = LinkType.Content,
                ImportanceScore = 50,
            });
        }

        for (var i = 0; i < navCount; i++)
        {
            links.Add(new LinkInfo
            {
                Url = $"https://example.com/nav{i}",
                DisplayText = $"Nav {i}",
                Type = LinkType.Navigation,
                ImportanceScore = 10,
            });
        }

        return links;
    }

    #region Article Classification

    [Fact]
    public void Classify_ArticlePage_FewLinks_ReturnsArticle()
    {
        var links = CreateLinks(contentCount: 3);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 1, "https://example.com/2024/01/story");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_NoLinks_ReturnsArticle()
    {
        var links = CreateLinks(contentCount: 0);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 1, "https://example.com/article");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_SidebarLinks_StillArticle()
    {
        // Article page with many sidebar links (12) but single <article> container
        var links = CreateLinks(contentCount: 12);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 1, "https://example.com/article");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_TenLinks_ReturnsArticle()
    {
        var links = CreateLinks(contentCount: 10);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 1, "https://example.com/article");
        result.Should().Be(PageClassification.Article);
    }

    #endregion

    #region LinkList Classification

    [Fact]
    public void Classify_ManyLinks_ManyArticleContainers_ReturnsLinkList()
    {
        // NYT homepage: 50+ content links, 20+ <article> containers
        var links = CreateLinks(contentCount: 50);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 20, "https://www.nytimes.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_ManyLinks_NotArticle_ReturnsLinkList()
    {
        // HN-style page: 30 content links, no <article> tags, not an article
        var links = CreateLinks(contentCount: 30);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 0, "https://news.ycombinator.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_FifteenLinks_ThreeArticleContainers_ReturnsLinkList()
    {
        var links = CreateLinks(contentCount: 15);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 3, "https://example.com/section/tech");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_SectionUrl_FiveLinks_ReturnsLinkList()
    {
        var links = CreateLinks(contentCount: 5);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 0, "https://example.com/section/politics");
        result.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Unknown Classification

    [Fact]
    public void Classify_FewLinks_NotArticle_ReturnsUnknown()
    {
        var links = CreateLinks(contentCount: 2);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 0, "https://example.com/about");
        result.Should().Be(PageClassification.Unknown);
    }

    [Fact]
    public void Classify_NoLinks_NotArticle_ReturnsUnknown()
    {
        var links = CreateLinks(contentCount: 0);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 0, "https://example.com/page");
        result.Should().Be(PageClassification.Unknown);
    }

    [Fact]
    public void Classify_ModerateLinks_NoArticleSignals_ReturnsUnknown()
    {
        // 8 content links, no article indicators — ambiguous
        var links = CreateLinks(contentCount: 8);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 0, "https://example.com/page");
        result.Should().Be(PageClassification.Unknown);
    }

    #endregion

    #region IsSectionUrlPattern

    [Theory]
    [InlineData("https://www.nytimes.com/", true)]
    [InlineData("https://www.nytimes.com", true)]
    [InlineData("https://example.com/section/world", true)]
    [InlineData("https://example.com/topic/climate", true)]
    [InlineData("https://example.com/tag/javascript", true)]
    [InlineData("https://example.com/category/tech", true)]
    [InlineData("https://example.com/latest", true)]
    [InlineData("https://example.com/archive", true)]
    [InlineData("https://example.com/technology", true)]
    [InlineData("https://example.com/politics", true)]
    [InlineData("https://example.com/opinion", true)]
    [InlineData("https://example.com/2024/01/15/article-slug", false)]
    [InlineData("https://example.com/about", false)]
    [InlineData("https://example.com/contact-us", false)]
    [InlineData("https://example.com/p/some-article", false)]
    public void IsSectionUrlPattern_CorrectlyIdentifiesSectionUrls(string url, bool expected)
    {
        PageClassifier.IsSectionUrlPattern(url).Should().Be(expected);
    }

    [Fact]
    public void IsSectionUrlPattern_EmptyUrl_ReturnsFalse()
    {
        PageClassifier.IsSectionUrlPattern("").Should().BeFalse();
        PageClassifier.IsSectionUrlPattern(null!).Should().BeFalse();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Classify_ArticlePageWithManyArticleContainers_ReturnsLinkList()
    {
        // Page where IsArticle returns true (has og:type=article) but has many <article>
        // containers — this is actually a section page with article cards
        var links = CreateLinks(contentCount: 20);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 10, "https://example.com/");

        // Even though IsArticle is true, 20 content links + 10 containers = LinkList
        // (isArticlePage && contentLinks > 10 && articleContainerCount <= 1 → Article,
        //  but articleContainerCount is 10, so it falls through to the 15+ && 3+ check)
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_EmptyLinks_ArticlePage_ReturnsArticle()
    {
        var result = PageClassifier.Classify([], isArticlePage: true, articleContainerCount: 1, "https://example.com/article");
        result.Should().Be(PageClassification.Article);
    }

    #endregion
}
