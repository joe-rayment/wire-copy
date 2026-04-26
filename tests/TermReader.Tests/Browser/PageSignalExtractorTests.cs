// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class PageSignalExtractorTests
{
    #region OgType

    [Fact]
    public void Extract_OgTypeArticle_DetectsCorrectly()
    {
        var html = """
            <html><head>
                <meta property="og:type" content="article">
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.OgType.Should().Be("article");
    }

    [Fact]
    public void Extract_OgTypeWebsite_DetectsCorrectly()
    {
        var html = """
            <html><head>
                <meta property="og:type" content="website">
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.OgType.Should().Be("website");
    }

    [Fact]
    public void Extract_NoOgType_ReturnsNull()
    {
        var html = "<html><head></head><body></body></html>";
        var signals = PageSignalExtractor.Extract(html);
        signals.OgType.Should().BeNull();
    }

    #endregion

    #region LdJson

    [Fact]
    public void Extract_LdJsonNewsArticle_DetectsCorrectly()
    {
        var html = """
            <html><head>
                <script type="application/ld+json">
                {"@type": "NewsArticle", "headline": "Test"}
                </script>
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.LdJsonType.Should().Be("NewsArticle");
    }

    [Fact]
    public void Extract_LdJsonWebSite_DetectsCorrectly()
    {
        var html = """
            <html><head>
                <script type="application/ld+json">
                {"@type": "WebSite", "name": "Test"}
                </script>
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.LdJsonType.Should().Be("WebSite");
    }

    [Fact]
    public void Extract_LdJsonArray_FindsFirstRelevantType()
    {
        var html = """
            <html><head>
                <script type="application/ld+json">
                [{"@type": "BreadcrumbList"}, {"@type": "NewsArticle", "headline": "Test"}]
                </script>
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.LdJsonType.Should().Be("NewsArticle");
    }

    [Fact]
    public void Extract_LdJsonSkipsBoilerplateTypes()
    {
        // BreadcrumbList, Organization, ImageObject, Person should be skipped
        var html = """
            <html><head>
                <script type="application/ld+json">
                {"@type": "Organization", "name": "Test Corp"}
                </script>
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.LdJsonType.Should().BeNull();
    }

    [Fact]
    public void Extract_MultipleLdJsonScripts_FindsArticle()
    {
        // First script has Organization, second has NewsArticle
        var html = """
            <html><head>
                <script type="application/ld+json">
                {"@type": "Organization", "name": "Test"}
                </script>
                <script type="application/ld+json">
                {"@type": "NewsArticle", "headline": "Breaking"}
                </script>
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.LdJsonType.Should().Be("NewsArticle");
    }

    [Fact]
    public void Extract_MalformedLdJson_DoesNotThrow()
    {
        var html = """
            <html><head>
                <script type="application/ld+json">not json at all</script>
            </head><body></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.LdJsonType.Should().BeNull();
    }

    #endregion

    #region ArticleContainerCount

    [Fact]
    public void Extract_SingleArticleTag_CountsOne()
    {
        var html = "<html><body><article><p>Content</p></article></body></html>";
        var signals = PageSignalExtractor.Extract(html);
        signals.ArticleContainerCount.Should().Be(1);
    }

    [Fact]
    public void Extract_MultipleArticleTags_CountsAll()
    {
        var html = """
            <html><body>
                <article>Card 1</article>
                <article>Card 2</article>
                <article>Card 3</article>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.ArticleContainerCount.Should().Be(3);
    }

    [Fact]
    public void Extract_NoArticleTags_CountsZero()
    {
        var html = "<html><body><div>Content</div></body></html>";
        var signals = PageSignalExtractor.Extract(html);
        signals.ArticleContainerCount.Should().Be(0);
    }

    #endregion

    #region RoleArticleCount

    [Fact]
    public void Extract_RoleArticleElements_CountsCorrectly()
    {
        // The Verge homepage uses role="article" on quick-post cards
        var html = """
            <html><body>
                <div role="article">Post 1</div>
                <div role="article">Post 2</div>
                <div role="article">Post 3</div>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.RoleArticleCount.Should().Be(3);
    }

    #endregion

    #region HasArticleBodyClass

    [Fact]
    public void Extract_ArticleBodyClass_DetectsPresence()
    {
        var html = """
            <html><body>
                <div class="duet--article--article-body-component">Content</div>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.HasArticleBodyClass.Should().BeTrue();
    }

    [Fact]
    public void Extract_EntryContentClass_DetectsPresence()
    {
        var html = """
            <html><body>
                <div class="entry-content">Content</div>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.HasArticleBodyClass.Should().BeTrue();
    }

    [Fact]
    public void Extract_NoArticleBodyClass_ReturnsFalse()
    {
        var html = "<html><body><div class=\"sidebar\">Nav</div></body></html>";
        var signals = PageSignalExtractor.Extract(html);
        signals.HasArticleBodyClass.Should().BeFalse();
    }

    #endregion

    #region DeepParagraphCount

    [Fact]
    public void Extract_DeepParagraphs_CountsOnlyLong()
    {
        var longText = new string('x', 250);
        var html = $"""
            <html><body><main>
                <p>{longText}</p>
                <p>{longText}</p>
                <p>{longText}</p>
                <p>Short para</p>
            </main></body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.DeepParagraphCount.Should().Be(3);
    }

    [Fact]
    public void Extract_ParagraphsInNavExcluded()
    {
        var longText = new string('x', 250);
        var html = $"""
            <html><body>
                <main><p>{longText}</p></main>
                <nav><p>{longText}</p></nav>
                <footer><p>{longText}</p></footer>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.DeepParagraphCount.Should().Be(1);
    }

    #endregion

    #region Other signals

    [Fact]
    public void Extract_H1Present_Detected()
    {
        var html = "<html><body><h1>Article Headline</h1></body></html>";
        var signals = PageSignalExtractor.Extract(html);
        signals.HasH1.Should().BeTrue();
    }

    [Fact]
    public void Extract_TimeElements_Counted()
    {
        var html = """
            <html><body>
                <time datetime="2024-01-01">Jan 1</time>
                <time datetime="2024-01-02">Jan 2</time>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);
        signals.TimeElementCount.Should().Be(2);
    }

    [Fact]
    public void Extract_MainElement_Detected()
    {
        var html = "<html><body><main>Content</main></body></html>";
        var signals = PageSignalExtractor.Extract(html);
        signals.HasMainElement.Should().BeTrue();
    }

    [Fact]
    public void Extract_RoleMain_Detected()
    {
        var html = "<html><body><div role=\"main\">Content</div></body></html>";
        var signals = PageSignalExtractor.Extract(html);
        signals.HasMainElement.Should().BeTrue();
    }

    [Fact]
    public void Extract_EmptyHtml_ReturnsDefaults()
    {
        var signals = PageSignalExtractor.Extract(string.Empty);
        signals.OgType.Should().BeNull();
        signals.LdJsonType.Should().BeNull();
        signals.ArticleContainerCount.Should().Be(0);
        signals.HasArticleBodyClass.Should().BeFalse();
        signals.DeepParagraphCount.Should().Be(0);
    }

    #endregion

    #region Real-World Signal Combinations

    [Fact]
    public void Extract_VergeArticleSignals_AllPresent()
    {
        // Simulates The Verge article HTML structure
        var longText = new string('A', 300);
        var ldJson = "{\"@type\": \"NewsArticle\", \"headline\": \"SpaceX IPO\"}";
        var html = "<html><head>" +
            "<meta property=\"og:type\" content=\"article\">" +
            "<script type=\"application/ld+json\">" + ldJson + "</script>" +
            "</head><body>" +
            "<main id=\"content\">" +
            "<article>" +
            "<h1>SpaceX IPO Could Value Company at Over $1 Trillion</h1>" +
            "<div class=\"duet--article--article-body-component\">" +
            "<p>" + longText + "</p>" +
            "<p>" + longText + "</p>" +
            "<p>" + longText + "</p>" +
            "<p>" + longText + "</p>" +
            "</div>" +
            "<time datetime=\"2026-04-21\">April 21</time>" +
            "</article></main></body></html>";

        var signals = PageSignalExtractor.Extract(html);

        signals.OgType.Should().Be("article");
        signals.LdJsonType.Should().Be("NewsArticle");
        signals.ArticleContainerCount.Should().Be(1);
        signals.RoleArticleCount.Should().Be(0);
        signals.HasArticleBodyClass.Should().BeTrue();
        signals.HasH1.Should().BeTrue();
        signals.DeepParagraphCount.Should().BeGreaterOrEqualTo(3);
        signals.HasMainElement.Should().BeTrue();
    }

    [Fact]
    public void Extract_VergeHomepageSignals_ListingPage()
    {
        // Simulates The Verge homepage HTML structure
        var html = """
            <html><head>
                <meta property="og:type" content="website">
                <script type="application/ld+json">
                {"@type": "NewsMediaOrganization", "name": "The Verge"}
                </script>
            </head><body>
                <main id="content" class="_1e7jslx0">
                    <div role="article"><p>Quick post 1</p><time>Apr 20</time></div>
                    <div role="article"><p>Quick post 2</p><time>Apr 20</time></div>
                    <div role="article"><p>Quick post 3</p><time>Apr 19</time></div>
                    <div role="article"><p>Quick post 4</p><time>Apr 19</time></div>
                    <time>Apr 18</time><time>Apr 17</time><time>Apr 16</time>
                </main>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);

        signals.OgType.Should().Be("website");
        signals.LdJsonType.Should().BeNull("NewsMediaOrganization is filtered as boilerplate");
        signals.ArticleContainerCount.Should().Be(0);
        signals.RoleArticleCount.Should().Be(4);
        signals.HasArticleBodyClass.Should().BeFalse();
        signals.HasH1.Should().BeFalse();
        signals.HasMainElement.Should().BeTrue();
    }

    [Fact]
    public void Extract_WikipediaSignals_MinimalMetadata()
    {
        // Wikipedia: no og:type, no ld+json, no <article>, but has main + h1 + paragraphs
        var longText = new string('W', 300);
        var html = $"""
            <html><head></head><body>
                <main class="mw-body">
                    <h1>Terminal emulator</h1>
                    <div class="mw-parser-output">
                        <p>{longText}</p>
                        <p>{longText}</p>
                        <p>{longText}</p>
                        <p>{longText}</p>
                    </div>
                </main>
            </body></html>
            """;

        var signals = PageSignalExtractor.Extract(html);

        signals.OgType.Should().BeNull();
        signals.LdJsonType.Should().BeNull();
        signals.ArticleContainerCount.Should().Be(0);
        signals.HasH1.Should().BeTrue();
        signals.DeepParagraphCount.Should().BeGreaterOrEqualTo(3);
        signals.HasMainElement.Should().BeTrue();
    }

    #endregion
}
