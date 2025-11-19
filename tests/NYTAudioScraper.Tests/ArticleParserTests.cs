// <copyright file="ArticleParserTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Infrastructure.Parsing;
using Xunit;

namespace NYTAudioScraper.Tests;

public class ArticleParserTests
{
    private readonly ArticleParser _parser;
    private readonly ILogger<ArticleParser> _logger;

    public ArticleParserTests()
    {
        _logger = Substitute.For<ILogger<ArticleParser>>();
        _parser = new ArticleParser(_logger);
    }

    [Fact]
    public void ParseArticle_WithValidHtml_ReturnsArticle()
    {
        // Arrange
        var html = @"
            <html>
                <head>
                    <title>Test Article Title</title>
                    <meta property='og:title' content='Test Article Title' />
                    <meta name='author' content='John Doe' />
                    <meta property='article:section' content='Technology' />
                    <meta property='article:published_time' content='2025-01-15T10:00:00Z' />
                </head>
                <body>
                    <h1 data-testid='headline'>Test Article Title</h1>
                    <section name='articleBody'>
                        <p>This is the first paragraph of the article.</p>
                        <p>This is the second paragraph with more content.</p>
                    </section>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/technology/test-article.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().NotBeNull();
        result!.Title.Should().Be("Test Article Title");
        result.Author.Should().Be("John Doe");
        result.Section.Should().Be("Technology");
        result.Content.Should().Contain("first paragraph");
        result.Content.Should().Contain("second paragraph");
        result.Url.Should().Be(url);
        result.PublishedDate.Year.Should().Be(2025);
    }

    [Fact]
    public void ParseArticle_WithMissingTitle_ReturnsNull()
    {
        // Arrange
        var html = @"
            <html>
                <body>
                    <section name='articleBody'>
                        <p>Content without a title.</p>
                    </section>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/test.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseArticle_WithMissingContent_ReturnsNull()
    {
        // Arrange
        var html = @"
            <html>
                <head>
                    <title>Test Article</title>
                </head>
                <body>
                    <h1>Test Article</h1>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/test.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseArticle_WithAlternativeTitleSelector_ExtractsTitle()
    {
        // Arrange
        var html = @"
            <html>
                <head>
                    <meta property='og:title' content='Article from Meta Tag' />
                </head>
                <body>
                    <section name='articleBody'>
                        <p>Some content here.</p>
                    </section>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/test.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().NotBeNull();
        result!.Title.Should().Be("Article from Meta Tag");
    }

    [Fact]
    public void ParseArticle_WithMultipleParagraphs_CombinesContent()
    {
        // Arrange
        var html = @"
            <html>
                <head>
                    <title>Multi-Paragraph Article</title>
                </head>
                <body>
                    <h1>Multi-Paragraph Article</h1>
                    <section name='articleBody'>
                        <p>First paragraph.</p>
                        <p>Second paragraph.</p>
                        <p>Third paragraph.</p>
                    </section>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/test.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().NotBeNull();
        result!.Content.Should().Contain("First paragraph");
        result.Content.Should().Contain("Second paragraph");
        result.Content.Should().Contain("Third paragraph");
    }

    [Fact]
    public void ParseArticle_WithHtmlEntities_DecodesContent()
    {
        // Arrange
        var html = @"
            <html>
                <head>
                    <title>Article with Entities</title>
                </head>
                <body>
                    <h1>Article with Entities</h1>
                    <section name='articleBody'>
                        <p>Content with &amp; ampersand and &quot;quotes&quot;.</p>
                    </section>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/test.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().NotBeNull();
        result!.Content.Should().Contain("&");
        result.Content.Should().Contain("\"");
    }

    [Fact]
    public void ParseArticle_WithInvalidHtml_ReturnsNull()
    {
        // Arrange
        var html = "Not valid HTML at all!";
        var url = "https://www.nytimes.com/2025/01/15/test.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseArticle_GeneratesCorrectArticleId_FromUrl()
    {
        // Arrange
        var html = @"
            <html>
                <head><title>Test</title></head>
                <body>
                    <h1>Test</h1>
                    <section name='articleBody'><p>Content</p></section>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/technology/test-article-slug.html";

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("test-article-slug.html");
    }

    [Fact]
    public void ParseArticle_WithNoPublishedDate_UsesCurrentDate()
    {
        // Arrange
        var html = @"
            <html>
                <head><title>Test Article</title></head>
                <body>
                    <h1>Test Article</h1>
                    <section name='articleBody'><p>Content</p></section>
                </body>
            </html>";
        var url = "https://www.nytimes.com/2025/01/15/test.html";
        var beforeParse = DateTime.UtcNow;

        // Act
        var result = _parser.ParseArticle(html, url);

        // Assert
        result.Should().NotBeNull();
        result!.PublishedDate.Should().BeCloseTo(beforeParse, TimeSpan.FromSeconds(5));
    }
}
