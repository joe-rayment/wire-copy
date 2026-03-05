// <copyright file="ReadableContentTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Entities.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

public class ReadableContentTests
{
    [Fact]
    public void Create_WithValidData_ReturnsContent()
    {
        // Arrange
        var title = "Test Article";
        var text = "This is the article content with multiple words for testing.";
        var paragraphs = new List<string> { "Paragraph 1", "Paragraph 2" };

        // Act
        var content = ReadableContent.Create(title, text, paragraphs);

        // Assert
        content.Should().NotBeNull();
        content.Title.Should().Be(title);
        content.CleanedText.Should().Be(text);
        content.Paragraphs.Should().HaveCount(2);
    }

    [Fact]
    public void Create_WithAuthorAndDate_SetsOptionalFields()
    {
        // Arrange
        var title = "Test Article";
        var text = "Content here.";
        var paragraphs = new List<string> { "Content here." };
        var author = "Jane Doe";
        var publishedDate = new DateTime(2024, 1, 22);

        // Act
        var content = ReadableContent.Create(title, text, paragraphs, author, publishedDate);

        // Assert
        content.Author.Should().Be(author);
        content.PublishedDate.Should().Be(publishedDate);
    }

    [Fact]
    public void Create_WithEmptyTitle_ThrowsArgumentException()
    {
        // Arrange
        var text = "Content";
        var paragraphs = new List<string> { "Content" };

        // Act & Assert
        var act = () => ReadableContent.Create("", text, paragraphs);
        act.Should().Throw<ArgumentException>().WithParameterName("title");
    }

    [Fact]
    public void Create_WithWhitespaceTitle_ThrowsArgumentException()
    {
        // Arrange
        var text = "Content";
        var paragraphs = new List<string> { "Content" };

        // Act & Assert
        var act = () => ReadableContent.Create("   ", text, paragraphs);
        act.Should().Throw<ArgumentException>().WithParameterName("title");
    }

    [Fact]
    public void Create_WithEmptyContent_ThrowsArgumentException()
    {
        // Arrange
        var paragraphs = new List<string> { "Paragraph" };

        // Act & Assert
        var act = () => ReadableContent.Create("Title", "", paragraphs);
        act.Should().Throw<ArgumentException>().WithParameterName("cleanedText");
    }

    [Fact]
    public void Create_WithEmptyParagraphs_ThrowsArgumentException()
    {
        // Act & Assert
        var act = () => ReadableContent.Create("Title", "Content", new List<string>());
        act.Should().Throw<ArgumentException>().WithParameterName("paragraphs");
    }

    [Fact]
    public void Create_WithNullParagraphs_ThrowsArgumentException()
    {
        // Act & Assert
        var act = () => ReadableContent.Create("Title", "Content", null!);
        act.Should().Throw<ArgumentException>().WithParameterName("paragraphs");
    }

    [Fact]
    public void WordCount_CalculatesCorrectly()
    {
        // Arrange
        var text = "One two three four five six seven eight nine ten";
        var paragraphs = new List<string> { text };

        // Act
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Assert
        content.WordCount.Should().Be(10);
    }

    [Fact]
    public void WordCount_HandlesMultipleSpaces()
    {
        // Arrange
        var text = "One  two   three    four";
        var paragraphs = new List<string> { text };

        // Act
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Assert
        content.WordCount.Should().Be(4);
    }

    [Fact]
    public void EstimatedReadingMinutes_CalculatesCorrectly()
    {
        // Arrange - 400 words should be 2 minutes at 200 wpm
        var words = string.Join(" ", Enumerable.Repeat("word", 400));
        var paragraphs = new List<string> { words };

        // Act
        var content = ReadableContent.Create("Title", words, paragraphs);

        // Assert
        content.EstimatedReadingMinutes.Should().Be(2);
    }

    [Fact]
    public void EstimatedReadingMinutes_MinimumIsOneMinute()
    {
        // Arrange - Very short content
        var text = "Short";
        var paragraphs = new List<string> { text };

        // Act
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Assert
        content.EstimatedReadingMinutes.Should().Be(1);
    }

    [Fact]
    public void GetPreview_ReturnsTruncatedContent()
    {
        // Arrange
        var text = new string('x', 500);
        var paragraphs = new List<string> { text };
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Act
        var preview = content.GetPreview(100);

        // Assert
        preview.Should().HaveLength(103); // 100 chars + "..."
        preview.Should().EndWith("...");
    }

    [Fact]
    public void GetPreview_ShortContent_ReturnsFullText()
    {
        // Arrange
        var text = "Short content";
        var paragraphs = new List<string> { text };
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Act
        var preview = content.GetPreview(200);

        // Assert
        preview.Should().Be(text);
        preview.Should().NotEndWith("...");
    }

    [Fact]
    public void GetPreview_DefaultLength_Is200()
    {
        // Arrange
        var text = new string('x', 500);
        var paragraphs = new List<string> { text };
        var content = ReadableContent.Create("Title", text, paragraphs);

        // Act
        var preview = content.GetPreview();

        // Assert
        preview.Should().HaveLength(203); // 200 chars + "..."
    }

    [Fact]
    public void GetMetadataString_WithAllFields_FormatsCorrectly()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content here for testing the metadata string output.",
            new List<string> { "Content here for testing the metadata string output." },
            "Jane Doe",
            new DateTime(2024, 1, 22));

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().Contain("By Jane Doe");
        metadata.Should().Contain("Jan 22, 2024");
        metadata.Should().Contain("min read");
    }

    [Fact]
    public void GetMetadataString_WithoutAuthor_OmitsAuthor()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content",
            new List<string> { "Content" },
            null,
            new DateTime(2024, 1, 22));

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().NotContain("By");
        metadata.Should().Contain("Jan 22, 2024");
    }

    [Fact]
    public void GetMetadataString_WithoutDate_OmitsDate()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content",
            new List<string> { "Content" },
            "Jane Doe",
            null);

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().Contain("By Jane Doe");
        metadata.Should().NotContain("2024");
    }

    [Fact]
    public void GetMetadataString_MinimalContent_ShowsReadingTime()
    {
        // Arrange
        var content = ReadableContent.Create(
            "Title",
            "Content",
            new List<string> { "Content" });

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().Contain("min read");
    }

    [Fact]
    public async Task Extractor_WithNoParagraphs_ReturnsNull()
    {
        // Arrange - HTML with article tag but no <p> tags with > 50 chars
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html><body><article><h1>Title Only</h1><span>Short</span></article></body></html>";
        var url = "https://example.com/article";

        // Act
        var result = await extractor.ExtractAsync(html, url);

        // Assert
        result.Should().BeNull("no paragraphs with sufficient content means no readable content");
    }

    [Fact]
    public async Task Extractor_WithNoTitle_UsesFallbackTitle()
    {
        // Arrange - article with paragraphs but no h1/title/og:title
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var longParagraph = new string('x', 60);
        var html = $@"<html><body><article>
            <p>{longParagraph} first paragraph text here.</p>
            <p>{longParagraph} second paragraph text here.</p>
            <p>{longParagraph} third paragraph text here.</p>
        </article></body></html>";
        var url = "https://example.com/article";

        // Act
        var result = await extractor.ExtractAsync(html, url);

        // Assert
        result.Should().NotBeNull();
        result!.Title.Should().Be("Untitled Article");
    }

    private static string BuildArticleHtml(string headExtra = "", string bodyExtra = "")
    {
        var longParagraph = new string('x', 60);
        return $@"<html><head>{headExtra}</head><body><article>
            <h1>Test</h1>
            <p>{longParagraph} first paragraph content here.</p>
            <p>{longParagraph} second paragraph content here.</p>
            <p>{longParagraph} third paragraph content here.</p>
            {bodyExtra}
        </article></body></html>";
    }

    private static async Task<string?> ExtractAuthorFromHtml(string html)
    {
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var result = await extractor.ExtractAsync(html, "https://example.com/article");
        return result?.Author;
    }

    [Fact]
    public async Task Extractor_MetaNameAuthor_WithName_ReturnsName()
    {
        var html = BuildArticleHtml("<meta name='author' content='Jane Doe' />");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Jane Doe");
    }

    [Fact]
    public async Task Extractor_ArticleAuthorUrl_DoesNotReturnRawUrl()
    {
        var html = BuildArticleHtml("<meta property='article:author' content='https://www.nytimes.com/by/blacki-migliozzi' />");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().NotStartWith("http");
    }

    [Fact]
    public async Task Extractor_ArticleAuthorUrl_ExtractsNameFromPath()
    {
        var html = BuildArticleHtml("<meta property='article:author' content='https://www.nytimes.com/by/blacki-migliozzi' />");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Blacki Migliozzi");
    }

    [Fact]
    public async Task Extractor_JsonLd_AuthorObject_ReturnsName()
    {
        var html = BuildArticleHtml(@"<script type='application/ld+json'>
            {""@type"":""NewsArticle"",""author"":{""@type"":""Person"",""name"":""John Smith""}}
        </script>");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("John Smith");
    }

    [Fact]
    public async Task Extractor_JsonLd_AuthorArray_ReturnsCommaSeparatedNames()
    {
        var html = BuildArticleHtml(@"<script type='application/ld+json'>
            {""@type"":""NewsArticle"",""author"":[{""name"":""Alice""},{""name"":""Bob""}]}
        </script>");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Alice, Bob");
    }

    [Fact]
    public async Task Extractor_JsonLd_AuthorString_ReturnsName()
    {
        var html = BuildArticleHtml(@"<script type='application/ld+json'>
            {""@type"":""Article"",""author"":""Sarah Connor""}
        </script>");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Sarah Connor");
    }

    [Fact]
    public async Task Extractor_BylineElement_WithLink_ReturnsLinkText()
    {
        var html = BuildArticleHtml(bodyExtra: "<div class='byline'><a href='/author/jane'>Jane Doe</a></div>");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Jane Doe");
    }

    [Fact]
    public async Task Extractor_ItempropAuthor_ReturnsName()
    {
        var html = BuildArticleHtml(bodyExtra: "<span itemprop='author'><span itemprop='name'>Alex Writer</span></span>");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Alex Writer");
    }

    [Fact]
    public async Task Extractor_MetaNameAuthorUrl_FallsBackToUrlParsing()
    {
        var html = BuildArticleHtml("<meta name='author' content='https://www.washingtonpost.com/people/mary-kay-johnson/' />");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Mary Kay Johnson");
    }

    [Fact]
    public async Task Extractor_MetaNameAuthor_PreferredOverArticleAuthorUrl()
    {
        var html = BuildArticleHtml(
            "<meta name='author' content='Real Author' />" +
            "<meta property='article:author' content='https://example.com/by/someone-else' />");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Real Author");
    }

    [Fact]
    public async Task Extractor_UrlWithNoParseableName_ReturnsNull()
    {
        var html = BuildArticleHtml("<meta property='article:author' content='https://example.com/' />");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().BeNull();
    }

    [Fact]
    public async Task Extractor_HyphenatedUrlPath_ProperTitleCasing()
    {
        var html = BuildArticleHtml("<meta property='article:author' content='https://example.com/authors/mary-kay-johnson' />");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Mary Kay Johnson");
    }
}
