// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkExtractorSectionTitleTests
{
    #region ExtractSectionTitle — semantic section containers

    [Fact]
    public void ExtractSectionTitle_SectionWithH2_ReturnsSectionTitle()
    {
        var html = @"
            <section>
                <h2>Top Stories</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Top Stories");
    }

    [Fact]
    public void ExtractSectionTitle_ArticleWithH3_ReturnsNull()
    {
        // <article> wraps individual articles, not sections.
        // Its heading is the article's own title, not a section name.
        var html = @"
            <article>
                <h3>World News</h3>
                <a href=""https://example.com/article"">Article Title</a>
            </article>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull("article elements are individual items, not section containers");
    }

    [Fact]
    public void ExtractSectionTitle_DivWithRoleRegion_ReturnsSectionTitle()
    {
        var html = @"
            <div role=""region"">
                <h2>Opinion</h2>
                <a href=""https://example.com/opinion"">Opinion Piece</a>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Opinion");
    }

    [Fact]
    public void ExtractSectionTitle_SectionWithAriaLabel_PrefersAriaLabel()
    {
        var html = @"
            <section aria-label=""Breaking News"">
                <h2>Some Other Heading</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Breaking News");
    }

    [Fact]
    public void ExtractSectionTitle_NestedAnchor_FindsSectionFromAncestor()
    {
        var html = @"
            <section>
                <h2>Sports</h2>
                <div>
                    <ul>
                        <li>
                            <a href=""https://example.com/sports"">Game Results</a>
                        </li>
                    </ul>
                </div>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Sports");
    }

    [Fact]
    public void ExtractSectionTitle_MultipleHeadings_ReturnsFirst()
    {
        var html = @"
            <section>
                <h2>First Heading</h2>
                <h3>Second Heading</h3>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("First Heading");
    }

    #endregion

    #region ExtractSectionTitle — no section container (flat pages)

    [Fact]
    public void ExtractSectionTitle_PlainDiv_ReturnsNull()
    {
        var html = @"
            <div>
                <h2>Some Heading</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull("plain div without role=region is not a section container");
    }

    [Fact]
    public void ExtractSectionTitle_NoContainer_ReturnsNull()
    {
        var html = @"<a href=""https://example.com"">Bare Link</a>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractSectionTitle_SectionWithoutHeading_ReturnsNull()
    {
        var html = @"
            <section>
                <p>Just some text</p>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractSectionTitle_NoSectionContainer_ReturnsNull()
    {
        // No section container in ancestry — just plain divs
        var html = @"
            <div>
                <h2>Not A Section</h2>
                <div><div><div>
                    <a href=""https://example.com/article"">Article Title</a>
                </div></div></div>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull("no section container exists in ancestry");
    }

    [Fact]
    public void ExtractSectionTitle_DeeplyNested_FindsSectionAtAnyDepth()
    {
        // Anchor is 9 levels deep from section — no depth limit, should still find it
        var html = @"
            <section>
                <h2>Deep Section</h2>
                <div><div><div><div><div><div><div><div><div>
                    <a href=""https://example.com/article"">Article Title</a>
                </div></div></div></div></div></div></div></div></div>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Deep Section", "section container should be found regardless of nesting depth");
    }

    [Fact]
    public void ExtractSectionTitle_SixLevelsDeep_MatchesNytDomStructure()
    {
        // Matches real NYT DOM: anchor → div → article → li → ol → div → section
        var html = @"
            <section>
                <h2>The Front Page</h2>
                <div><div><div><div><div><div>
                    <a href=""https://example.com/article"">Article Title</a>
                </div></div></div></div></div></div>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("The Front Page", "anchor is within 8-level walk limit");
    }

    #endregion

    #region ExtractSectionTitle — generic heading filtering

    [Theory]
    [InlineData("More")]
    [InlineData("Also")]
    [InlineData("Trending")]
    [InlineData("Most Read")]
    [InlineData("Most Popular")]
    [InlineData("Recommended")]
    [InlineData("Related")]
    [InlineData("See Also")]
    [InlineData("Latest")]
    [InlineData("You Might Like")]
    public void ExtractSectionTitle_GenericHeading_ReturnsNull(string genericTitle)
    {
        var html = $@"
            <section>
                <h2>{genericTitle}</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull($"'{genericTitle}' is a generic heading that should be filtered");
    }

    [Fact]
    public void ExtractSectionTitle_GenericHeading_CaseInsensitive()
    {
        var html = @"
            <section>
                <h2>TRENDING</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull("generic heading filtering should be case-insensitive");
    }

    #endregion

    #region ExtractSectionTitle — title length validation

    [Fact]
    public void ExtractSectionTitle_TooShort_ReturnsNull()
    {
        var html = @"
            <section>
                <h2>Hi</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull("titles shorter than 3 characters should be filtered");
    }

    [Fact]
    public void ExtractSectionTitle_ExactlyThreeChars_ReturnsTitle()
    {
        var html = @"
            <section>
                <h2>Art</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Art");
    }

    [Fact]
    public void ExtractSectionTitle_TooLong_ReturnsNull()
    {
        var longTitle = new string('A', 81);
        var html = $@"
            <section>
                <h2>{longTitle}</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull("titles longer than 80 characters should be filtered");
    }

    [Fact]
    public void ExtractSectionTitle_ExactlyEightyChars_ReturnsTitle()
    {
        var title = new string('A', 80);
        var html = $@"
            <section>
                <h2>{title}</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be(title);
    }

    #endregion

    #region ExtractSectionTitle — aria-label edge cases

    [Fact]
    public void ExtractSectionTitle_AriaLabelGeneric_SkipsToHeading()
    {
        var html = @"
            <section aria-label=""More"">
                <h2>Technology</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Technology", "generic aria-label should be skipped, falling back to heading");
    }

    [Fact]
    public void ExtractSectionTitle_AriaLabelTooShort_SkipsToHeading()
    {
        var html = @"
            <section aria-label=""Hi"">
                <h2>Business</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Business", "short aria-label should be skipped, falling back to heading");
    }

    [Fact]
    public void ExtractSectionTitle_EmptyAriaLabel_SkipsToHeading()
    {
        var html = @"
            <section aria-label="""">
                <h2>Science</h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Science");
    }

    #endregion

    #region ExtractSectionTitle — whitespace handling

    [Fact]
    public void ExtractSectionTitle_HeadingWithExtraWhitespace_NormalizesText()
    {
        var html = @"
            <section>
                <h2>  Top   Stories  </h2>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Top Stories");
    }

    #endregion

    #region ExtractSectionTitle — non-direct child headings

    [Fact]
    public void ExtractSectionTitle_HeadingNestedInDiv_FindsHeadingAtDepth2()
    {
        var html = @"
            <section>
                <div>
                    <h2>Nested Heading</h2>
                </div>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Nested Heading", "headings nested up to depth 2 should be found");
    }

    #endregion

    #region Integration — SectionTitle populated on content links only

    [Fact]
    public async Task ExtractLinksAsync_ContentLinkInNestedArticle_FindsParentSectionTitle()
    {
        var logger = Substitute.For<ILogger<LinkExtractor>>();
        var extractor = new LinkExtractor(logger);

        var html = @"<html><body>
            <section>
                <h2>Technology</h2>
                <article>
                    <a href=""https://example.com/tech-article"">A Very Long Article Title That Exceeds Thirty Characters</a>
                </article>
            </section>
        </body></html>";

        var links = await extractor.ExtractLinksAsync(html, "https://example.com");

        links.Should().ContainSingle();
        links[0].Type.Should().Be(LinkType.Content);
        links[0].SectionTitle.Should().Be("Technology",
            "article has no heading, so walk continues up to section which has one");
    }

    [Fact]
    public async Task ExtractLinksAsync_ContentLinkInSection_PopulatesSectionTitle()
    {
        var logger = Substitute.For<ILogger<LinkExtractor>>();
        var extractor = new LinkExtractor(logger);

        var html = @"<html><body>
            <section>
                <h2>Technology</h2>
                <a href=""https://example.com/tech-article"">A Very Long Article Title That Exceeds Thirty Characters</a>
            </section>
        </body></html>";

        var links = await extractor.ExtractLinksAsync(html, "https://example.com");

        links.Should().ContainSingle();
        links[0].Type.Should().Be(LinkType.Content);
        links[0].SectionTitle.Should().Be("Technology");
    }

    [Fact]
    public async Task ExtractLinksAsync_NavigationLink_DoesNotGetSectionTitle()
    {
        var logger = Substitute.For<ILogger<LinkExtractor>>();
        var extractor = new LinkExtractor(logger);

        var html = @"<html><body>
            <nav>
                <section>
                    <h2>Menu</h2>
                    <a href=""https://example.com/page"">Nav Link</a>
                </section>
            </nav>
        </body></html>";

        var links = await extractor.ExtractLinksAsync(html, "https://example.com");

        links.Should().ContainSingle();
        links[0].Type.Should().Be(LinkType.Navigation);
        links[0].SectionTitle.Should().BeNull("navigation links should not get section titles");
    }

    #endregion

    #region NYT-style semantic sections

    [Fact]
    public void ExtractSectionTitle_NytStyleSections_ExtractsFromSemanticMarkup()
    {
        var html = @"
            <section aria-label=""Opinion"">
                <h2>Opinion</h2>
                <div class=""story-wrapper"">
                    <a href=""https://nytimes.com/opinion/article"">Opinion Article Title</a>
                </div>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        // Should find the section within 5 ancestors (a -> div -> section)
        result.Should().Be("Opinion");
    }

    [Fact]
    public void ExtractSectionTitle_MultipleNytSections_EachGetsOwnTitle()
    {
        var html = @"
            <div>
                <section aria-label=""World"">
                    <h2>World</h2>
                    <a href=""https://nytimes.com/world/1"" id=""world-link"">World Article</a>
                </section>
                <section aria-label=""Business"">
                    <h2>Business</h2>
                    <a href=""https://nytimes.com/biz/1"" id=""biz-link"">Business Article</a>
                </section>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var worldAnchor = doc.DocumentNode.SelectSingleNode("//a[@id='world-link']");
        var bizAnchor = doc.DocumentNode.SelectSingleNode("//a[@id='biz-link']");

        LinkExtractor.ExtractSectionTitle(worldAnchor).Should().Be("World");
        LinkExtractor.ExtractSectionTitle(bizAnchor).Should().Be("Business");
    }

    [Fact]
    public void ExtractSectionTitle_NytArticleWithDataTestid_SkipsToParentSection()
    {
        // NYT wraps articles in <article data-testid> with <div data-testid> wrappers.
        // These should NOT be section containers; the parent <section> should provide the title.
        var html = @"
            <section aria-label=""The Front Page"">
                <h2>The Front Page</h2>
                <article data-testid=""block-article"">
                    <div data-testid=""story-wrapper"">
                        <h3><a href=""https://nytimes.com/article"">Article Headline Goes Here</a></h3>
                    </div>
                </article>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("The Front Page",
            "should skip article/div data-testid containers and find parent section");
    }

    [Fact]
    public void ExtractSectionTitle_DivWithDataTestid_NotSectionContainer()
    {
        // data-testid is a React testing attribute, not a semantic section indicator
        var html = @"
            <div data-testid=""story-card"">
                <h3>Story Heading</h3>
                <a href=""https://example.com/story"">Story Link</a>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().BeNull("data-testid divs are not semantic section containers");
    }

    #endregion

    #region Heading tag variants

    [Theory]
    [InlineData("h1")]
    [InlineData("h2")]
    [InlineData("h3")]
    [InlineData("h4")]
    [InlineData("h5")]
    [InlineData("h6")]
    public void ExtractSectionTitle_AllHeadingLevels_Supported(string headingTag)
    {
        var html = $@"
            <section>
                <{headingTag}>Section Title</{headingTag}>
                <a href=""https://example.com/article"">Article Title</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var result = LinkExtractor.ExtractSectionTitle(anchor);

        result.Should().Be("Section Title");
    }

    #endregion
}
