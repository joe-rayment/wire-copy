// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkExtractorTests
{
    private readonly LinkExtractor _sut;
    private readonly ILogger<LinkExtractor> _logger;

    public LinkExtractorTests()
    {
        _logger = Substitute.For<ILogger<LinkExtractor>>();
        _sut = new LinkExtractor(_logger);
    }

    /// <summary>
    /// Parameterized test covering HTML parsing robustness across different formatting
    /// variations: minified, unclosed tags, missing head close, normal multi-link.
    /// Consolidated from 6 redundant [Fact] tests that all verified "extract links from HTML."
    /// </summary>
    [Theory]
    [MemberData(nameof(HtmlParsingTestCases))]
    public async Task ExtractLinksAsync_WithVariousHtmlFormats_ShouldExtractLinks(
        string testName, string html, string baseUrl, int expectedMinCount, string? expectedDisplayText)
    {
        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().NotBeNull(testName);
        links.Should().HaveCountGreaterOrEqualTo(expectedMinCount,
            $"{testName}: expected at least {expectedMinCount} link(s)");

        if (expectedDisplayText != null)
        {
            links.Should().ContainSingle(l => l.DisplayText == expectedDisplayText,
                $"{testName}: expected link with display text '{expectedDisplayText}'");
        }
    }

    public static TheoryData<string, string, string, int, string?> HtmlParsingTestCases => new()
    {
        {
            "Minified HTML (example.com)",
            @"<!doctype html><html lang=""en""><head><title>Example Domain</title><meta name=""viewport"" content=""width=device-width, initial-scale=1""><style>body{background:#eee}</style></head><body><div><h1>Example Domain</h1><p>This domain is for use in documentation examples without needing permission.</p><p><a href=""https://iana.org/domains/example"">Learn more</a></p></div></body></html>",
            "https://example.com",
            1,
            "Learn more"
        },
        {
            "Unclosed <p> tags",
            @"<!doctype html><html><body><p>Text<p><a href=""https://iana.org/domains/example"">Learn more</a></body></html>",
            "https://example.com",
            1,
            "Learn more"
        },
        {
            "Missing </head> close tag",
            @"<!doctype html><html><head><title>Test</title><style>body{}</style><body><div><p><a href=""https://iana.org/domains/example"">Learn more</a></div></body></html>",
            "https://example.com",
            1,
            "Learn more"
        },
        {
            "Normal well-formed HTML with multiple links",
            @"<html><body><a href=""https://example.com/page1"">Page One</a><a href=""https://example.com/page2"">Page Two</a></body></html>",
            "https://example.com",
            2,
            null
        },
        {
            "Severely malformed HTML with nested tag issues",
            @"<html><body><div><p>Text<a href=""https://example.com/page1"">Link One<div><span>Nested</a></p></div></body>",
            "https://example.com",
            0, // may or may not find links — just should not throw
            null
        },
    };

    [Fact]
    public async Task ExtractLinksAsync_WithExternalLink_ShouldClassifyAsExternal()
    {
        var html = @"<html><body><a href=""https://iana.org/domains/example"">Learn more</a></body></html>";
        var baseUrl = "https://example.com";

        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        links.Should().HaveCount(1);
        links[0].DisplayText.Should().Be("Learn more");
        links[0].Type.Should().Be(LinkType.External);
    }

    [Fact]
    public async Task ExtractLinksAsync_ShouldFilterAdLinks()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""https://example.com/article"">Real Article Title</a>
                <a href=""https://sponsor.com/promo"">Created for Some Company</a>
                <a href=""https://sponsor.com/ad"">Sponsored Content</a>
                <div class=""advertisement"">
                    <a href=""https://ad.com/link"">Click Here</a>
                </div>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1, "only the real article should remain after filtering ads");
        links[0].DisplayText.Should().Be("Real Article Title");
    }

    [Fact]
    public async Task ExtractLinksAsync_WithHackerNewsHtml_ShouldExtractManyLinks()
    {
        // Arrange - Simplified HN-style HTML
        var html = @"
            <html>
            <body>
                <table>
                    <tr class=""athing""><td class=""title""><a href=""https://example.com/story1"">First Story About Technology and Innovation in Modern Computing</a></td></tr>
                    <tr class=""athing""><td class=""title""><a href=""https://example.com/story2"">Second Story About Science and Discovery</a></td></tr>
                    <tr><td><a href=""https://news.ycombinator.com"">Hacker News</a></td></tr>
                </table>
            </body>
            </html>";
        var baseUrl = "https://news.ycombinator.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCountGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task ExtractLinksAsync_ShouldHandleSubdomainsCorrectly()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""https://www.example.com/page"">Same Domain WWW</a>
                <a href=""https://blog.example.com/post"">Same Domain Subdomain</a>
                <a href=""https://other.com/page"">Different Domain</a>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(3);

        // www.example.com and blog.example.com should NOT be marked as external
        var wwwLink = links.First(l => l.Url.Contains("www.example.com"));
        var blogLink = links.First(l => l.Url.Contains("blog.example.com"));
        var otherLink = links.First(l => l.Url.Contains("other.com"));

        wwwLink.Type.Should().NotBe(WireCopy.Domain.Enums.Browser.LinkType.External);
        blogLink.Type.Should().NotBe(WireCopy.Domain.Enums.Browser.LinkType.External);
        otherLink.Type.Should().Be(WireCopy.Domain.Enums.Browser.LinkType.External);
    }

    [Fact]
    public async Task ExtractLinksAsync_WithEmptyHtml_ShouldReturnEmptyList()
    {
        // Arrange
        var html = "<html><body></body></html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractLinksAsync_ShouldSkipJavascriptLinks()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""javascript:void(0)"">JS Link</a>
                <a href=""#section"">Anchor Link</a>
                <a href=""https://example.com/real"">Real Link</a>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].DisplayText.Should().Be("Real Link");
    }

    [Fact]
    public async Task ExtractLinksAsync_WithNoAnchors_ShouldReturnEmptyList()
    {
        // Arrange - HTML with content but zero <a> tags
        var html = @"<html><body><h1>Hello World</h1><p>Some paragraph text.</p></body></html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractLinksAsync_WithRelativeUrls_ShouldResolveCorrectly()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""/about"">About Us</a>
                <a href=""articles/latest"">Latest Articles From Our Publication</a>
                <a href=""../other"">Other Section With Long Description Text</a>
            </body>
            </html>";
        var baseUrl = "https://example.com/section/page";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().NotBeEmpty();
        links.Should().Contain(l => l.Url == "https://example.com/about");
        links.Should().Contain(l => l.Url == "https://example.com/section/articles/latest");
        links.Should().Contain(l => l.Url == "https://example.com/other");
    }

    #region GroupLinksByUrl Tests

    [Fact]
    public void GroupLinksByUrl_SameUrlSameParent_MergesDisplayText()
    {
        // Arrange — category label + headline in same parent
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/article-1", DisplayText = "News", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.card" },
            new() { Url = "https://example.com/article-1", DisplayText = "Big Headline About Something", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.card" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("News: Big Headline About Something");
        result[0].ImportanceScore.Should().Be(70);
    }

    [Fact]
    public void GroupLinksByUrl_SameUrlDifferentParent_FirstOccurrenceWins()
    {
        // Arrange — same URL in nav and in content area; first occurrence (earlier section) wins
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/page", DisplayText = "Home", Type = LinkType.Navigation, ImportanceScore = 30, ParentSelector = "nav.main" },
            new() { Url = "https://example.com/page", DisplayText = "Welcome to Our Homepage", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "main.content" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert — first occurrence wins, preserving document/editorial order
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("Home");
        result[0].ImportanceScore.Should().Be(30);
    }

    [Fact]
    public void GroupLinksByUrl_SameUrlDifferentParent_ImageAltReplacedByRealText()
    {
        // Arrange — first is image alt, second has real text; second replaces
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/page", DisplayText = "Photo caption", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.hero", IsFromImageAlt = true },
            new() { Url = "https://example.com/page", DisplayText = "Article Headline", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.content", IsFromImageAlt = false }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert — image-alt is replaced by real text
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("Article Headline");
        result[0].IsFromImageAlt.Should().BeFalse();
    }

    [Fact]
    public void GroupLinksByUrl_CrossSectionDedup_PropagatesMetadataFromLaterCopy()
    {
        // Arrange — first occurrence has no author, later copy from topic section has author
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/article", DisplayText = "Headline on Front Page", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "section.front-page", Author = null, SectionTitle = null },
            new() { Url = "https://example.com/article", DisplayText = "Headline in Business", Type = LinkType.Content, ImportanceScore = 50, ParentSelector = "section.business", Author = "Jane Smith", SectionTitle = "Business" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert — first occurrence wins, but metadata propagated from later copy
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("Headline on Front Page");
        result[0].Author.Should().Be("Jane Smith");
        result[0].SectionTitle.Should().Be("Business");
    }

    [Fact]
    public void GroupLinksByUrl_ThreeLinksInSameParent_MergesAll()
    {
        // Arrange — three links in the same card
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/article", DisplayText = "Politics", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.card" },
            new() { Url = "https://example.com/article", DisplayText = "Breaking News", Type = LinkType.Content, ImportanceScore = 50, ParentSelector = "div.card" },
            new() { Url = "https://example.com/article", DisplayText = "Major Policy Change Announced Today", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.card" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("Politics: Breaking News: Major Policy Change Announced Today");
        result[0].ImportanceScore.Should().Be(70);
    }

    [Fact]
    public void GroupLinksByUrl_DuplicateText_Deduplicates()
    {
        // Arrange — "Read More" appears twice in the same parent
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/article", DisplayText = "Read More", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.card" },
            new() { Url = "https://example.com/article", DisplayText = "Read More", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.card" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("Read More");
    }

    [Fact]
    public void GroupLinksByUrl_SubstringText_RemovesRedundant()
    {
        // Arrange — "News" is a substring of "News Today"
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/article", DisplayText = "News", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.card" },
            new() { Url = "https://example.com/article", DisplayText = "News Today", Type = LinkType.Content, ImportanceScore = 50, ParentSelector = "div.card" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("News Today");
    }

    [Fact]
    public void GroupLinksByUrl_ImageAltAndRealText_PrefersRealText()
    {
        // Arrange — image alt + real headline in same parent
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/article", DisplayText = "Article thumbnail", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.card", IsFromImageAlt = true },
            new() { Url = "https://example.com/article", DisplayText = "Great Article Title", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.card", IsFromImageAlt = false }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert
        result.Should().HaveCount(1);
        result[0].DisplayText.Should().Be("Great Article Title");
        result[0].IsFromImageAlt.Should().BeFalse();
    }

    [Fact]
    public void GroupLinksByUrl_DifferentUrls_NoGrouping()
    {
        // Arrange — different URLs in the same parent should NOT merge
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/article-1", DisplayText = "First Article", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.card" },
            new() { Url = "https://example.com/article-2", DisplayText = "Second Article", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.card" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert
        result.Should().HaveCount(2);
        result[0].DisplayText.Should().Be("First Article");
        result[1].DisplayText.Should().Be("Second Article");
    }

    [Fact]
    public void GroupLinksByUrl_PreservesDocumentOrder()
    {
        // Arrange — merged entry should appear at position of first occurrence
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/first", DisplayText = "First Link", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.a" },
            new() { Url = "https://example.com/second", DisplayText = "Category", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.b" },
            new() { Url = "https://example.com/second", DisplayText = "Second Link Headline", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.b" },
            new() { Url = "https://example.com/third", DisplayText = "Third Link", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.c" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert
        result.Should().HaveCount(3);
        result[0].Url.Should().Be("https://example.com/first");
        result[1].Url.Should().Be("https://example.com/second");
        result[1].DisplayText.Should().Be("Category: Second Link Headline");
        result[2].Url.Should().Be("https://example.com/third");
    }

    [Fact]
    public void GroupLinksByUrl_SameUrlSameParent_NotAdjacent_DoesNotMerge()
    {
        // Arrange — two /politics/ links with identical ParentSelector but separated
        // by a different-URL link (the macleans.ca scenario)
        var links = new List<LinkInfo>
        {
            new() { Url = "https://macleans.ca/politics/", DisplayText = "Politics", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.article-card" },
            new() { Url = "https://macleans.ca/politics/article-1", DisplayText = "Headline One About Policy", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.article-card" },
            new() { Url = "https://macleans.ca/politics/", DisplayText = "Politics", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "div.article-card" },
            new() { Url = "https://macleans.ca/politics/article-2", DisplayText = "Headline Two About Economy", Type = LinkType.Content, ImportanceScore = 70, ParentSelector = "div.article-card" }
        };

        // Act
        var result = LinkExtractor.GroupLinksByUrl(links);

        // Assert — the two /politics/ links should NOT merge since they're not adjacent.
        // Phase 2 cross-parent dedup picks the best representative, so we get 3 unique URLs.
        result.Should().HaveCount(3);
        result.Should().Contain(l => l.Url == "https://macleans.ca/politics/article-1");
        result.Should().Contain(l => l.Url == "https://macleans.ca/politics/article-2");
        result.Should().Contain(l => l.Url == "https://macleans.ca/politics/");

        // The /politics/ representative should be a single "Politics" text, not merged
        var politicsLink = result.First(l => l.Url == "https://macleans.ca/politics/");
        politicsLink.DisplayText.Should().Be("Politics");
    }

    [Fact]
    public async Task ExtractLinksAsync_ShouldSkipArticleCategoryLabelLinks()
    {
        // Arrange — links with data-link-type="article category label" should be excluded
        var html = @"
            <html>
            <body>
                <div class=""article-card"">
                    <a href=""https://example.com/politics/"" data-link-type=""article category label"">Politics</a>
                    <a href=""https://example.com/politics/article-1"">Big Headline About Important Policy Changes</a>
                </div>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert — category label link is filtered out, only the headline remains
        links.Should().HaveCount(1);
        links[0].Url.Should().Contain("article-1");
        links[0].DisplayText.Should().Be("Big Headline About Important Policy Changes");
    }

    #endregion

    #region DetermineLinkType Heuristic Tests

    [Theory]
    [InlineData("Short Nav Link", LinkType.Navigation)]
    [InlineData("About Us", LinkType.Navigation)]
    [InlineData("Home", LinkType.Navigation)]
    [InlineData("Privacy Policy Terms", LinkType.Navigation)]
    [InlineData("Bay Area Considers the Unthinkable", LinkType.Content)]  // 34 chars - real headline
    [InlineData("A Link That Is Exactly Forty Nine Characters Long", LinkType.Content)]
    [InlineData("A Fifty Character Title That Describes a Real Story", LinkType.Content)]
    [InlineData("Major Policy Change Announced by Government Officials Today in Ottawa", LinkType.Content)]
    public void ClassifyLink_TextLengthHeuristic_ClassifiesCorrectly(string displayText, LinkType expected)
    {
        // Arrange - no parent selector, same domain, so the fallback heuristic applies
        var url = "https://example.com/some-page";
        var baseUrl = "https://example.com";

        // Act
        var result = _sut.ClassifyLink(url, displayText, parentSelector: null, baseUrl);

        // Assert
        result.Type.Should().Be(expected);
    }

    [Fact]
    public void ClassifyLink_ArticleUrlPattern_ShortHeadline_ShouldBeContent()
    {
        // Arrange - URL with date pattern indicates article, even with short text
        var url = "https://example.com/2024/01/15/short-headline";
        var displayText = "Short Headline";  // 14 chars - under 25, but URL pattern is article
        var baseUrl = "https://example.com";

        // Act
        var result = _sut.ClassifyLink(url, displayText, parentSelector: null, baseUrl);

        // Assert - URL date pattern should make this Content
        result.Type.Should().Be(LinkType.Content);
    }

    [Fact]
    public void ClassifyLink_NonArticleUrl_ShortText_ShouldBeNavigation()
    {
        // Arrange - short text, no semantic parent, no article URL pattern
        var url = "https://example.com/section/about";
        var displayText = "About This Site";  // 15 chars
        var baseUrl = "https://example.com";

        // Act
        var result = _sut.ClassifyLink(url, displayText, parentSelector: null, baseUrl);

        // Assert - short text with generic URL should be Navigation
        result.Type.Should().Be(LinkType.Navigation);
    }

    [Fact]
    public void ClassifyLink_SidebarRelatedLink_WithLongText_ShouldBeNavigation()
    {
        // Arrange - link inside a "related" sidebar with 35+ char title
        // Previously this would have been misclassified as Content due to the 30-char heuristic
        var url = "https://example.com/related-article";
        var displayText = "Related: Why Climate Policy Matters";  // 34 chars - was misclassified
        var parentSelector = "div.related-stories > ul > li";
        var baseUrl = "https://example.com";

        // Act
        var result = _sut.ClassifyLink(url, displayText, parentSelector, baseUrl);

        // Assert - "related" class in parent should make this Navigation
        result.Type.Should().Be(LinkType.Navigation);
    }

    [Fact]
    public void ClassifyLink_WidgetLink_WithLongText_ShouldBeNavigation()
    {
        // Arrange - link inside a widget container
        var url = "https://example.com/trending-article";
        var displayText = "Trending: The Economy Is Changing Fast";  // 38 chars
        var parentSelector = "aside.widget > div > a";
        var baseUrl = "https://example.com";

        // Act
        var result = _sut.ClassifyLink(url, displayText, parentSelector, baseUrl);

        // Assert - "widget" class and "aside" tag should make this Navigation
        result.Type.Should().Be(LinkType.Navigation);
    }

    [Fact]
    public void ClassifyLink_RecommendedSidebar_WithLongText_ShouldBeNavigation()
    {
        // Arrange - link in a "recommended" section
        var url = "https://example.com/recommended-article";
        var displayText = "You Might Also Enjoy This Longer Article Title";  // 47 chars
        var parentSelector = "section.recommended > div > a";
        var baseUrl = "https://example.com";

        // Act
        var result = _sut.ClassifyLink(url, displayText, parentSelector, baseUrl);

        // Assert
        result.Type.Should().Be(LinkType.Navigation);
    }

    [Fact]
    public void ClassifyLink_ArticleContainer_WithLongText_ShouldBeContent()
    {
        // Arrange - link inside an article container should still be Content
        var url = "https://example.com/main-story";
        var displayText = "Government Announces Major Infrastructure Plan";
        var parentSelector = "article.story > div > h2 > a";
        var baseUrl = "https://example.com";

        // Act
        var result = _sut.ClassifyLink(url, displayText, parentSelector, baseUrl);

        // Assert
        result.Type.Should().Be(LinkType.Content);
    }

    [Fact]
    public async Task ExtractLinksAsync_SidebarRelatedLinks_ShouldBeNavigation()
    {
        // Arrange - realistic sidebar with related article links that have 35+ char titles
        var html = @"
            <html>
            <body>
                <article>
                    <a href=""https://example.com/main-article"">Main Article With a Sufficiently Long Title for Content</a>
                </article>
                <aside class=""related"">
                    <h3>Related Stories</h3>
                    <a href=""https://example.com/related-1"">Why the Economy Is Shifting Toward Tech</a>
                    <a href=""https://example.com/related-2"">Scientists Discover New Species in Amazon</a>
                </aside>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(3);

        var mainLink = links.First(l => l.Url.Contains("main-article"));
        mainLink.Type.Should().Be(LinkType.Content, "link inside <article> should be Content");

        var related1 = links.First(l => l.Url.Contains("related-1"));
        related1.Type.Should().Be(LinkType.Navigation, "link inside <aside class='related'> should be Navigation");

        var related2 = links.First(l => l.Url.Contains("related-2"));
        related2.Type.Should().Be(LinkType.Navigation, "link inside <aside class='related'> should be Navigation");
    }

    #endregion

    #region Inline vs Headline Link Detection

    [Fact]
    public async Task ExtractLinksAsync_HeadlineLink_ShouldBeContent()
    {
        // Arrange — link inside a heading element inside an article
        var html = @"
            <html>
            <body>
                <article>
                    <h2><a href=""https://example.com/story"">Major Climate Bill Passes Senate in Historic Vote</a></h2>
                </article>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].Type.Should().Be(LinkType.Content, "headline links inside headings should remain Content");
        links[0].ImportanceScore.Should().BeGreaterThan(70, "headline links get an importance boost");
    }

    [Fact]
    public async Task ExtractLinksAsync_InlineLink_ShouldBeNavigation()
    {
        // Arrange — inline reference link inside a paragraph with surrounding text
        var html = @"
            <html>
            <body>
                <article>
                    <p>The president <a href=""https://example.com/speech"">told reporters at a press conference</a> that the new policy would take effect immediately and would affect millions of people across the country.</p>
                </article>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].Type.Should().Be(LinkType.Navigation,
            "inline reference links inside paragraphs with surrounding text should be Navigation");
    }

    [Fact]
    public async Task ExtractLinksAsync_MixedHeadlineAndInlineLinks_ClassifiesCorrectly()
    {
        // Arrange — section page with both headline links and inline references
        var html = @"
            <html>
            <body>
                <article>
                    <h2><a href=""https://example.com/headline-1"">Oil Prices Surge as OPEC Cuts Production Targets</a></h2>
                    <p>The decision <a href=""https://example.com/inline-1"">causes oil prices to surge</a> across global markets, affecting consumers and businesses alike.</p>
                    <h3><a href=""https://example.com/headline-2"">Tech Giants Report Record Quarterly Earnings</a></h3>
                    <p>Analysts say this <a href=""https://example.com/inline-2"">told CBS News</a> that the trend is likely to continue through the next fiscal year.</p>
                </article>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        var headline1 = links.First(l => l.Url.Contains("headline-1"));
        var headline2 = links.First(l => l.Url.Contains("headline-2"));
        var inline1 = links.First(l => l.Url.Contains("inline-1"));
        var inline2 = links.First(l => l.Url.Contains("inline-2"));

        headline1.Type.Should().Be(LinkType.Content, "h2 headline should be Content");
        headline2.Type.Should().Be(LinkType.Content, "h3 headline should be Content");
        inline1.Type.Should().Be(LinkType.Navigation, "inline reference should be Navigation");
        inline2.Type.Should().Be(LinkType.Navigation, "inline reference should be Navigation");
    }

    [Fact]
    public async Task ExtractLinksAsync_StandaloneContentLink_ShouldRemainContent()
    {
        // Arrange — standalone link in an article, not inside a heading or paragraph
        var html = @"
            <html>
            <body>
                <article>
                    <div class=""card"">
                        <a href=""https://example.com/standalone"">Standalone Article Card With a Long Title Here</a>
                    </div>
                </article>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].Type.Should().Be(LinkType.Content,
            "standalone links in article containers that aren't inline should remain Content");
    }

    #endregion

    #region Section Page Metadata Guard

    [Fact]
    public async Task ExtractLinksAsync_SectionPage_DoesNotApplyPageMetadataToAllLinks()
    {
        // Arrange — section page with multiple <article> containers and a JSON-LD author
        var html = @"
            <html>
            <head>
                <script type=""application/ld+json"">{""author"":{""name"":""Brad Plumer""},""datePublished"":""2024-03-09""}</script>
            </head>
            <body>
                <article>
                    <h2><a href=""https://example.com/article-1"">First Article About Climate Change and Its Global Impact</a></h2>
                </article>
                <article>
                    <h2><a href=""https://example.com/article-2"">Second Article About Technology Innovation and AI</a></h2>
                </article>
                <article>
                    <h2><a href=""https://example.com/article-3"">Third Article About Economic Policy Changes</a></h2>
                </article>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert — page-level metadata should NOT be applied when multiple <article> containers exist
        links.Should().HaveCount(3);
        links.Should().OnlyContain(l => l.Author == null,
            "section page with multiple <article> containers should not apply page-level author to all links");
    }

    [Fact]
    public async Task ExtractLinksAsync_SingleArticlePage_AppliesPageMetadata()
    {
        // Arrange — single-article page with JSON-LD author
        var html = @"
            <html>
            <head>
                <script type=""application/ld+json"">{""author"":{""name"":""Jane Doe""},""datePublished"":""2024-03-09""}</script>
                <meta property=""article:published_time"" content=""2024-03-09T12:00:00Z"" />
            </head>
            <body>
                <article>
                    <h1>Main Article Title That Is Long Enough</h1>
                    <p>Article body text with enough words to be meaningful content.</p>
                    <a href=""https://example.com/related"">Related: Another Story About Similar Topic</a>
                </article>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert — single article page should apply page-level metadata
        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        contentLinks.Should().NotBeEmpty();
        contentLinks.Should().Contain(l => l.Author == "Jane Doe",
            "single-article page should apply page-level author as fallback");
    }

    #endregion

    #region NYT-Style Section Page Fixture

    [Fact]
    public async Task ExtractLinksAsync_NytSectionPage_HeadlinesAreContent_InlinesAreNavigation()
    {
        // Arrange — NYT Today's Paper style section page with multiple <article> containers
        var html = @"
            <html>
            <head>
                <script type=""application/ld+json"">{""author"":{""name"":""Brad Plumer""},""datePublished"":""2024-03-09""}</script>
                <meta property=""article:published_time"" content=""2024-03-09T12:00:00Z"" />
            </head>
            <body>
                <main>
                    <article>
                        <h3><a href=""https://nytimes.com/2024/03/09/climate/article-1.html"">Climate Scientists Warn of Accelerating Ice Sheet Loss</a></h3>
                        <p>New data shows that <a href=""https://nytimes.com/2024/03/09/science/ice-data.html"">causes oil prices to surge</a> across global markets as governments struggle to respond to the growing crisis.</p>
                        <span class=""byline"">By Brad Plumer</span>
                        <time datetime=""2024-03-09T10:00:00Z"">March 9, 2024</time>
                    </article>
                    <article>
                        <h3><a href=""https://nytimes.com/2024/03/09/politics/article-2.html"">Senate Passes Sweeping Infrastructure Bill After Months of Debate</a></h3>
                        <p>The legislation, which <a href=""https://nytimes.com/2024/03/09/politics/told-cbs.html"">told CBS News</a> was the result of bipartisan negotiations, will fund roads, bridges and broadband access.</p>
                        <span class=""byline"">By Carl Hulse</span>
                        <time datetime=""2024-03-09T08:00:00Z"">March 9, 2024</time>
                    </article>
                    <article>
                        <h3><a href=""https://nytimes.com/2024/03/09/tech/article-3.html"">Tech Giants Report Record Quarterly Earnings Amid AI Boom</a></h3>
                        <p>Investors were cautiously optimistic as the industry continues to shift toward AI-driven products and services.</p>
                        <span class=""byline"">By Erin Griffith</span>
                        <time datetime=""2024-03-09T07:00:00Z"">March 9, 2024</time>
                    </article>
                </main>
            </body>
            </html>";
        var baseUrl = "https://nytimes.com/section/todayspaper";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert — headline links
        var headlineLinks = links.Where(l => l.Url.Contains("article-")).ToList();
        headlineLinks.Should().HaveCount(3, "each article container has one headline link");
        headlineLinks.Should().OnlyContain(l => l.Type == LinkType.Content,
            "headline links inside <h3> should be Content");

        // Assert — inline links
        var inlineLinks = links.Where(l => l.Url.Contains("ice-data") || l.Url.Contains("told-cbs")).ToList();
        inlineLinks.Should().HaveCount(2, "two inline references exist");
        inlineLinks.Should().OnlyContain(l => l.Type == LinkType.Navigation,
            "inline references in paragraphs should be Navigation, not Content");

        // Assert — page-level metadata NOT applied uniformly to all links on section page
        // At most one link should have Brad Plumer (from per-container byline), not all of them
        var plumerLinks = headlineLinks.Where(l => l.Author != null && l.Author.Contains("Plumer")).ToList();
        plumerLinks.Count.Should().BeLessOrEqualTo(1,
            "page-level JSON-LD author should NOT be applied to all links on section pages");

        // The first article's per-container byline should still work
        var article1 = links.First(l => l.Url.Contains("article-1"));
        article1.Author.Should().Be("By Brad Plumer",
            "per-container byline extraction should still work");

        // Other articles should have their own authors
        var article2 = links.First(l => l.Url.Contains("article-2"));
        article2.Author.Should().Be("By Carl Hulse");
    }

    [Fact]
    public async Task ExtractLinksAsync_NytSectionPage_CorrectLinkCount()
    {
        // Arrange — 4 articles, each with one headline link
        var html = @"
            <html>
            <body>
                <main>
                    <article><h3><a href=""https://nytimes.com/a1"">First Article Headline About Important News Event</a></h3></article>
                    <article><h3><a href=""https://nytimes.com/a2"">Second Article With Different Topic Coverage</a></h3></article>
                    <article><h3><a href=""https://nytimes.com/a3"">Third Article Discussing Science and Technology</a></h3></article>
                    <article><h3><a href=""https://nytimes.com/a4"">Fourth Article on Political Developments Today</a></h3></article>
                </main>
            </body>
            </html>";
        var baseUrl = "https://nytimes.com/section/todayspaper";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(4, "each article container contributes exactly one headline link");
        links.Should().OnlyContain(l => l.Type == LinkType.Content);
    }

    [Fact]
    public async Task ExtractLinksAsync_NytSectionPage_PerArticleMetadataStaysPerArticle()
    {
        // Arrange — two articles with different authors
        var html = @"
            <html>
            <body>
                <article>
                    <h3><a href=""https://nytimes.com/art1"">Article One About Climate With Long Title</a></h3>
                    <span class=""byline"">By Alice Reporter</span>
                </article>
                <article>
                    <h3><a href=""https://nytimes.com/art2"">Article Two About Economy With Long Title</a></h3>
                    <span class=""byline"">By Bob Journalist</span>
                </article>
            </body>
            </html>";
        var baseUrl = "https://nytimes.com/section/todayspaper";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(2);
        var art1 = links.First(l => l.Url.Contains("art1"));
        var art2 = links.First(l => l.Url.Contains("art2"));

        art1.Author.Should().Be("By Alice Reporter", "per-container author should stay with its article");
        art2.Author.Should().Be("By Bob Journalist", "per-container author should stay with its article");
    }

    #endregion

    #region Section Title Extraction Tests

    [Fact]
    public async Task ExtractSectionTitle_HeadingNestedInWrapperDiv_ShouldExtractTitle()
    {
        // Arrange — heading is at depth 2 inside section (section > div > h2)
        var html = @"
            <html>
            <body>
                <section>
                    <div class=""section-header"">
                        <h2>World News</h2>
                    </div>
                    <a href=""https://example.com/2024/01/15/world-story"">Major International Development Unfolds Across Multiple Continents</a>
                </section>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].SectionTitle.Should().Be("World News",
            "heading nested in a wrapper div (depth 2) should be extracted as section title");
    }

    [Fact]
    public async Task ExtractSectionTitle_DivWithDataTestId_ShouldNotBeRecognizedAsSectionContainer()
    {
        // data-testid is a React testing attribute, not a semantic section indicator.
        // On React-heavy sites (e.g., NYT), nearly every wrapper div has data-testid,
        // which incorrectly turned article headlines into section titles.
        var html = @"
            <html>
            <body>
                <div data-testid=""block-opinion"">
                    <h3>Opinion</h3>
                    <a href=""https://example.com/2024/01/15/opinion-piece"">A Thoughtful Analysis of Current Events and Their Implications</a>
                </div>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].SectionTitle.Should().BeNull(
            "data-testid divs are not semantic section containers");
    }

    [Fact]
    public async Task ExtractSectionTitle_DivWithSectionClass_ShouldBeRecognizedAsSectionContainer()
    {
        // Arrange — div whose class contains "section" should be recognized
        var html = @"
            <html>
            <body>
                <div class=""news-section"">
                    <h2>Technology</h2>
                    <a href=""https://example.com/2024/01/15/tech-story"">Revolutionary New Technology Changes the Way People Work and Live</a>
                </div>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].SectionTitle.Should().Be("Technology",
            "div with class containing 'section' should be recognized as a section container");
    }

    #endregion
}
