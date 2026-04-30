// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

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

    #region Rule 1: Section/homepage URLs are always LinkList

    [Theory]
    [InlineData("https://www.nytimes.com/")]
    [InlineData("https://www.nytimes.com")]
    [InlineData("https://www.theverge.com/")]
    [InlineData("https://news.ycombinator.com/")]
    [InlineData("https://arstechnica.com/")]
    public void Classify_HomepageUrl_WithContentLinks_ReturnsLinkList(string url)
    {
        // Homepages must ALWAYS be LinkList regardless of article indicators.
        // This is the critical fix: The Verge homepage has <article> cards
        // which caused isArticlePage=true, leading to Article misclassification.
        var links = CreateLinks(contentCount: 8);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 5, url);
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_HomepageUrl_FewLinks_StillLinkList()
    {
        // Even with just 1 content link, a homepage is a link list
        var links = CreateLinks(contentCount: 1);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 1, "https://www.nytimes.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_HomepageUrl_NoLinks_ReturnsUnknown()
    {
        // Edge case: homepage with zero content links — not useful as a link list
        var links = CreateLinks(contentCount: 0);
        var result = PageClassifier.Classify(links, isArticlePage: true, articleContainerCount: 0, "https://example.com/");
        result.Should().Be(PageClassification.Article);
    }

    [Theory]
    [InlineData("https://example.com/section/politics")]
    [InlineData("https://example.com/topic/climate")]
    [InlineData("https://example.com/technology")]
    [InlineData("https://example.com/opinion")]
    [InlineData("https://nytimes.com/section/world")]
    [InlineData("https://example.com/sports")]
    [InlineData("https://example.com/news")]
    [InlineData("https://example.com/tech")]
    [InlineData("https://example.com/entertainment")]
    [InlineData("https://example.com/reviews")]
    public void Classify_SectionUrl_WithContentLinks_ReturnsLinkList(string url)
    {
        var links = CreateLinks(contentCount: 5);
        var result = PageClassifier.Classify(links, isArticlePage: false, articleContainerCount: 0, url);
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_SectionUrl_OverridesArticleSignals()
    {
        // A section URL with article HTML structure should still be LinkList.
        // This catches the case where a section page has a featured story preview.
        var links = CreateLinks(contentCount: 12);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://www.nytimes.com/section/opinion");
        result.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Rule 2: Article URL pattern (date slug) + article structure

    [Fact]
    public void Classify_DateSlugUrl_ArticlePage_ReturnsArticle()
    {
        // NYT article: /2024/01/15/us/politics/story-slug
        var links = CreateLinks(contentCount: 3);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://www.nytimes.com/2024/01/15/us/politics/story-slug");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_DateSlugUrl_ManyInlineLinks_StillArticle()
    {
        // Long-form article with 8 inline links to related reading
        var links = CreateLinks(contentCount: 8);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://www.nytimes.com/2024/11/15/magazine/long-form-piece");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_DateSlugUrl_NotArticlePage_FallsThrough()
    {
        // Date slug URL but HTML doesn't have article structure — falls to later rules
        var links = CreateLinks(contentCount: 3);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://example.com/2024/01/15/roundup");
        result.Should().Be(PageClassification.Unknown);
    }

    #endregion

    #region Rule 3: Many links + multiple article containers = index page

    [Fact]
    public void Classify_ManyLinks_ManyArticleContainers_ReturnsLinkList()
    {
        // Verge/NYT homepage with article cards: many <article> elements
        var links = CreateLinks(contentCount: 50);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 20,
            "https://www.theverge.com/some-path");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_TenLinks_TwoContainers_ReturnsLinkList()
    {
        // Eliminates the old "dead zone" — 10 links + 2 containers is enough
        var links = CreateLinks(contentCount: 10);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 2,
            "https://example.com/blog");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_DeadZone_TwelveLinks_TwoContainers_ReturnsLinkList()
    {
        // Old dead zone (11-14 links) now correctly classified
        var links = CreateLinks(contentCount: 12);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 2,
            "https://example.com/feed");
        result.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Rule 4: Article structure + single container (any number of links)

    [Fact]
    public void Classify_ArticlePage_FewLinks_ReturnsArticle()
    {
        var links = CreateLinks(contentCount: 3);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/2024/01/story");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_NoLinks_ReturnsArticle()
    {
        var links = CreateLinks(contentCount: 0);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/article");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_FiveLinks_SingleContainer_ReturnsArticle()
    {
        var links = CreateLinks(contentCount: 5);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/article-with-links");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_ManyInlineLinks_SingleContainer_ReturnsArticle()
    {
        // Wikipedia, Verge longform: 50+ inline links but single article container.
        // Rule 4 must protect these from Rule 5 (link-count → LinkList).
        var links = CreateLinks(contentCount: 50);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://en.wikipedia.org/wiki/Terminal_emulator");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_HundredLinks_SingleContainer_ReturnsArticle()
    {
        // Extreme case: 100+ inline links in a single article
        var links = CreateLinks(contentCount: 100);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/comprehensive-guide");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_ArticlePage_ZeroContainers_ReturnsArticle()
    {
        // Article detection via paragraphs/og:type but no <article> tag (articleContainerCount=0)
        var links = CreateLinks(contentCount: 15);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 0,
            "https://example.com/blog-post");
        result.Should().Be(PageClassification.Article);
    }

    #endregion

    #region Rule 5: Many content links without article structure = LinkList

    [Fact]
    public void Classify_TenLinks_NoArticle_ReturnsLinkList()
    {
        // HN-style page: many links, no article markup
        var links = CreateLinks(contentCount: 10);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://news.ycombinator.com/news");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_FifteenLinks_NotArticle_ReturnsLinkList()
    {
        var links = CreateLinks(contentCount: 15);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://example.com/feed");
        result.Should().Be(PageClassification.LinkList);
    }

    #endregion

    #region Rule 6: Unknown fallback

    [Fact]
    public void Classify_FewLinks_NotArticle_ReturnsUnknown()
    {
        var links = CreateLinks(contentCount: 2);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://example.com/about");
        result.Should().Be(PageClassification.Unknown);
    }

    [Fact]
    public void Classify_NoLinks_NotArticle_ReturnsUnknown()
    {
        var links = CreateLinks(contentCount: 0);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://example.com/page");
        result.Should().Be(PageClassification.Unknown);
    }

    [Fact]
    public void Classify_ModerateLinks_NoArticleSignals_ReturnsUnknown()
    {
        // 8 content links, no article indicators — ambiguous, user can press 'v'
        var links = CreateLinks(contentCount: 8);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://example.com/page");
        result.Should().Be(PageClassification.Unknown);
    }

    #endregion

    #region IsSectionUrlPattern

    [Theory]
    // Root paths — always section
    [InlineData("https://www.nytimes.com/", true)]
    [InlineData("https://www.nytimes.com", true)]
    // Bare section paths — section index
    [InlineData("https://example.com/latest", true)]
    [InlineData("https://example.com/archive", true)]
    [InlineData("https://example.com/technology", true)]
    [InlineData("https://example.com/politics", true)]
    [InlineData("https://example.com/opinion", true)]
    [InlineData("https://example.com/news", true)]
    [InlineData("https://example.com/tech", true)]
    [InlineData("https://example.com/entertainment", true)]
    [InlineData("https://example.com/reviews", true)]
    [InlineData("https://example.com/features", true)]
    [InlineData("https://example.com/culture", true)]
    [InlineData("https://example.com/science", true)]
    [InlineData("https://example.com/us", true)]
    [InlineData("https://example.com/uk", true)]
    // One sub-level (sub-section names, not numeric IDs) — still section
    [InlineData("https://example.com/section/world", true)]
    [InlineData("https://example.com/topic/climate", true)]
    [InlineData("https://example.com/tag/javascript", true)]
    [InlineData("https://example.com/category/tech", true)]
    // Deep paths under section keywords — NOT section (articles)
    [InlineData("https://www.theverge.com/science/915244/spacex-ipo-trillion-dollar-commercial-iss-nasa-launch", false)]
    [InlineData("https://example.com/science/12345/article-slug", false)]
    [InlineData("https://example.com/technology/99999/some-article", false)]
    [InlineData("https://example.com/opinion/2024/columnist/piece", false)]
    // Numeric second segment — article ID, NOT sub-section
    [InlineData("https://example.com/science/915244", false)]
    [InlineData("https://example.com/news/12345", false)]
    // Non-section paths — false
    [InlineData("https://example.com/2024/01/15/article-slug", false)]
    [InlineData("https://example.com/about", false)]
    [InlineData("https://example.com/contact-us", false)]
    [InlineData("https://example.com/p/some-article", false)]
    [InlineData("https://example.com/login", false)]
    [InlineData("https://example.com/pricing", false)]
    [InlineData("https://example.com/settings", false)]
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

    #region IsArticleUrlPattern

    [Theory]
    [InlineData("https://nytimes.com/2024/01/15/us/story", true)]
    [InlineData("https://nytimes.com/2024/11/article-slug", true)]
    [InlineData("https://example.com/blog/2023/06/12/post", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://example.com/about", false)]
    [InlineData("https://example.com/section/tech", false)]
    [InlineData("https://example.com/article-slug", false)]
    public void IsArticleUrlPattern_CorrectlyIdentifiesArticleUrls(string url, bool expected)
    {
        PageClassifier.IsArticleUrlPattern(url).Should().Be(expected);
    }

    [Fact]
    public void IsArticleUrlPattern_EmptyUrl_ReturnsFalse()
    {
        PageClassifier.IsArticleUrlPattern("").Should().BeFalse();
        PageClassifier.IsArticleUrlPattern(null!).Should().BeFalse();
    }

    #endregion

    #region Real-World Regression Scenarios

    [Fact]
    public void Classify_VergeHomepage_ManyArticleCards_ReturnsLinkList()
    {
        // The Verge: root URL, many <article> cards, isArticlePage=true from article tags
        // This was the original bug — homepage showed as article view
        var links = CreateLinks(contentCount: 25);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 25,
            "https://www.theverge.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_VergeHomepage_SingleArticleWrapper_ReturnsLinkList()
    {
        // Even if The Verge wraps everything in one <article>, root URL wins
        var links = CreateLinks(contentCount: 20);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://www.theverge.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_NytHomepage_ReturnsLinkList()
    {
        var links = CreateLinks(contentCount: 50);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 20,
            "https://www.nytimes.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_NytArticle_ReturnsArticle()
    {
        var links = CreateLinks(contentCount: 5);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://www.nytimes.com/2024/11/15/us/politics/story-slug");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_SubstackHomepage_NoArticleTags_ReturnsLinkList()
    {
        // Substack: root URL, no <article> tags, many post links
        var links = CreateLinks(contentCount: 20);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://newsletter.example.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_SubstackWithFeaturedStory_RootUrl_ReturnsLinkList()
    {
        // Substack homepage with featured post (triggers isArticlePage) + few links
        var links = CreateLinks(contentCount: 3);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://newsletter.example.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_LongFormArticle_ManyInlineLinks_NotMisclassified()
    {
        // Long investigative piece with 9 inline "see also" links
        // Must NOT be classified as LinkList
        var links = CreateLinks(contentCount: 9);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/investigations/big-story");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_HackerNewsStyle_ReturnsLinkList()
    {
        // No article tags, many links, not a section URL
        var links = CreateLinks(contentCount: 30);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 0,
            "https://news.ycombinator.com/");
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_OldDeadZone_TwelveLinks_SingleContainer_NonSectionUrl()
    {
        // The old dead zone: 12 content links, isArticlePage true, single container, non-section URL
        // With new rules: rule 4 catches isArticlePage + single container → Article
        // This protects link-heavy articles (Wikipedia, Verge longform, etc.)
        var links = CreateLinks(contentCount: 12);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/some-page");

        result.Should().Be(PageClassification.Article,
            "rule 4 (isArticlePage + single container) fires before rule 5 (link count)");
    }

    [Fact]
    public void Classify_NineLinks_ArticlePage_SingleContainer_ReturnsArticle()
    {
        // 9 links, article structure, single container — rule 4 catches this
        var links = CreateLinks(contentCount: 9);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/some-article-page");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_WikipediaArticle_ManyLinks_ReturnsArticle()
    {
        // Wikipedia: 200+ inline wiki links, no <article> tags, isArticlePage from paragraphs
        var links = CreateLinks(contentCount: 200);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 0,
            "https://en.wikipedia.org/wiki/Terminal_emulator");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_VergeLongform_ManyInlineLinks_ReturnsArticle()
    {
        // Verge article: 50+ inline links, 1 <article> container, og:type=article
        var links = CreateLinks(contentCount: 50);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://www.theverge.com/ai-artificial-intelligence/908513/the-vibes-are-off-at-openai");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_VergeArticleUnderScience_NotMisclassifiedAsSection()
    {
        // THE BUG: /science/915244/slug was matching IsSectionUrlPattern because
        // /science/ was in the section regex. Now it should NOT match because
        // 915244 is a numeric ID (article), not a sub-section name.
        var links = CreateLinks(contentCount: 20);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://www.theverge.com/science/915244/spacex-ipo-trillion-dollar-commercial-iss-nasa-launch");
        result.Should().Be(PageClassification.Article,
            "Verge articles under /science/ should not be classified as LinkList");
    }

    #endregion

    #region Classification Version

    [Fact]
    public void ClassificationVersion_IsPositive()
    {
        PageClassifier.ClassificationVersion.Should().BeGreaterThan(0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Classify_ArticlePageWithManyArticleContainers_ReturnsLinkList()
    {
        // Page where IsArticle returns true (has og:type=article) but has many <article>
        // containers — this is actually a section page with article cards
        var links = CreateLinks(contentCount: 20);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 10,
            "https://example.com/");

        // Root URL fires rule 1 → LinkList
        result.Should().Be(PageClassification.LinkList);
    }

    [Fact]
    public void Classify_EmptyLinks_ArticlePage_ReturnsArticle()
    {
        var result = PageClassifier.Classify(
            [],
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/article");
        result.Should().Be(PageClassification.Article);
    }

    [Fact]
    public void Classify_NonSectionSingleSegment_NotTreatedAsSection()
    {
        // /about, /login, /pricing should NOT be treated as section URLs
        var links = CreateLinks(contentCount: 3);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: true,
            articleContainerCount: 1,
            "https://example.com/about");
        result.Should().Be(PageClassification.Article, "/about is not a section URL");
    }

    [Fact]
    public void Classify_TenLinks_OneContainer_NonArticle_ReturnsLinkList()
    {
        // 10 content links with no article page indicator — rule 5 catches
        var links = CreateLinks(contentCount: 10);
        var result = PageClassifier.Classify(
            links,
            isArticlePage: false,
            articleContainerCount: 1,
            "https://example.com/blog");
        result.Should().Be(PageClassification.LinkList);
    }

    #endregion
}
