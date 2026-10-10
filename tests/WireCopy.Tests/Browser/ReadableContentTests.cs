// <copyright file="ReadableContentTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
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
        preview.Should().HaveLength(101); // 100 chars + "…"
        preview.Should().EndWith("\u2026");
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
        preview.Should().NotEndWith("\u2026");
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
        preview.Should().HaveLength(201); // 200 chars + "…"
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
        // Design byline spec (workspace-7t0a.6): 'Author · Date' — no 'By '
        // prefix, no read-time (that lives in the end-of-article footer).
        metadata.Should().Be("Jane Doe · Jan 22, 2024");
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
        metadata.Should().Be("Jan 22, 2024");
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
        metadata.Should().Be("Jane Doe");
    }

    [Fact]
    public void GetMetadataString_MinimalContent_IsEmpty()
    {
        // Arrange — no author, no date: the byline contributes nothing
        // (read-time moved to the end-of-article footer, workspace-7t0a.6).
        var content = ReadableContent.Create(
            "Title",
            "Content",
            new List<string> { "Content" });

        // Act
        var metadata = content.GetMetadataString();

        // Assert
        metadata.Should().BeEmpty();
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

    // Guards:   Yoast/WordPress put the Article (and its author) under a top-level "@graph"; the reader
    //           byline must come from there, skipping the non-author nodes listed before it.
    // Fails if: the JSON-LD walk only looks at the root object / array items and ignores "@graph".
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~Extractor_JsonLd_GraphArticle_ReturnsAuthorName"
    [Fact]
    public async Task Extractor_JsonLd_GraphArticle_ReturnsAuthorName()
    {
        var html = BuildArticleHtml(@"<script type='application/ld+json'>
            {""@context"":""https://schema.org"",""@graph"":[
                {""@type"":""WebSite"",""name"":""Example News""},
                {""@type"":""Article"",""author"":{""@type"":""Person"",""name"":""Ada""}}
            ]}
        </script>");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Ada");
    }

    // Guards:   an array-root JSON-LD holding non-object items (strings, numbers, null) must not abort the
    //           reader; the author is still found in the object item after them.
    // Fails if: TryGetProperty is called on an array item without checking it is an object (throws
    //           InvalidOperationException, the reader returns null).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~Extractor_JsonLd_ArrayRootWithOddItems_StillFindsAuthor"
    [Fact]
    public async Task Extractor_JsonLd_ArrayRootWithOddItems_StillFindsAuthor()
    {
        var html = BuildArticleHtml(@"<script type='application/ld+json'>
            [""stray string"", 42, null, {""@type"":""NewsArticle"",""author"":""Grace Hopper""}]
        </script>");
        var author = await ExtractAuthorFromHtml(html);
        author.Should().Be("Grace Hopper");
    }

    // Guards:   a JSON-LD author whose "name" is an object (e.g. {"@value":"Jo"}) must not blank the whole
    //           reader view; the article content is still returned (just without that author).
    // Fails if: the name is read with GetString() without checking it is a string (throws, the reader
    //           returns null and the user sees an empty article).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~Extractor_JsonLd_NameIsObject_StillReturnsContent"
    [Fact]
    public async Task Extractor_JsonLd_NameIsObject_StillReturnsContent()
    {
        var html = BuildArticleHtml(@"<script type='application/ld+json'>
            {""@type"":""NewsArticle"",""author"":{""name"":{""@value"":""Jo""}}}
        </script>");
        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        result.Should().NotBeNull("an odd author shape must not discard the readable content");
        result!.Paragraphs.Should().NotBeEmpty();
        result.Author.Should().BeNull();
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

    #region IsEmptyArticleShell

    [Fact]
    public void IsEmptyArticleShell_ArticleTagNoParagraphs_ReturnsTrue()
    {
        var html = "<html><body><article><h1>Title</h1><div>Loading...</div></article></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeTrue();
    }

    [Fact]
    public void IsEmptyArticleShell_ArticleTagWithShortParagraphs_ReturnsTrue()
    {
        var html = "<html><body><article><h1>Title</h1><p>Short</p><p>Also short</p></article></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeTrue();
    }

    [Fact]
    public void IsEmptyArticleShell_ArticleTagWithSubstantialParagraphs_ReturnsFalse()
    {
        var longText = new string('x', 60);
        var html = $"<html><body><article><h1>Title</h1><p>{longText}</p><p>{longText}</p></article></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeFalse();
    }

    [Fact]
    public void IsEmptyArticleShell_NoArticleIndicators_ReturnsFalse()
    {
        var html = "<html><body><div>Just a normal page with no article markers</div></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeFalse();
    }

    [Fact]
    public void IsEmptyArticleShell_OgTypeArticleNoParagraphs_ReturnsTrue()
    {
        var html = "<html><head><meta property='og:type' content='article' /></head><body><div>Shell</div></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeTrue();
    }

    [Fact]
    public void IsEmptyArticleShell_EntryContentClassNoParagraphs_ReturnsTrue()
    {
        var html = "<html><body><div class='entry-content'><span>Loading</span></div></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeTrue();
    }

    [Fact]
    public void IsEmptyArticleShell_PostContentClassNoParagraphs_ReturnsTrue()
    {
        var html = "<html><body><div class='post-content'></div></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeTrue();
    }

    [Fact]
    public void IsEmptyArticleShell_ArticleBodyClassWithContent_ReturnsFalse()
    {
        var longText = new string('x', 60);
        var html = $"<html><body><div class='article-body'><p>{longText}</p><p>{longText}</p></div></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeFalse();
    }

    [Fact]
    public void IsEmptyArticleShell_OnlyOneSubstantialParagraph_ReturnsTrue()
    {
        var longText = new string('x', 60);
        var html = $"<html><body><article><p>{longText}</p><p>Short</p></article></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeTrue();
    }

    [Fact]
    public void IsEmptyArticleShell_ArticleContentClassNoParagraphs_ReturnsTrue()
    {
        var html = "<html><body><div class='article-content'><span>Loading</span></div></body></html>";

        ReadableContentExtractor.IsEmptyArticleShell(html).Should().BeTrue();
    }

    #endregion

    #region Improved Extraction

    [Fact]
    public async Task ExtractParagraphs_FindsContent_InBemStyleContainers()
    {
        // Arrange - HTML using BEM class naming (article__body)
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var p1 = "This is the first paragraph of the article with plenty of content to read through.";
        var p2 = "The second paragraph continues the story with additional details and information for readers.";
        var p3 = "A third paragraph wraps up the section with concluding thoughts and final remarks here.";
        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='article__body'>
                    <p>{p1}</p>
                    <p>{p2}</p>
                    <p>{p3}</p>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert
        result.Should().NotBeNull();
        result!.Paragraphs.Should().HaveCount(3);
        result.Paragraphs[0].Should().Contain("first paragraph");
    }

    [Fact]
    public async Task ExtractParagraphs_FallsToNextContentArea_WhenFirstHasFewParagraphs()
    {
        // Arrange - Two content areas: first has 1 paragraph, second has 5
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var shortP = "This is a single paragraph in the first content area with enough characters to count.";
        var longPs = string.Join("\n", Enumerable.Range(1, 5).Select(i =>
            $"<p>Paragraph number {i} in the second content area with substantial text content for extraction testing.</p>"));
        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='article-body'>
                    <p>{shortP}</p>
                </div>
                <div class='entry-content'>
                    {longPs}
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert
        result.Should().NotBeNull();
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3, "should fall through to second content area with more paragraphs");
    }

    [Fact]
    public async Task ExtractParagraphs_LargestParagraphBlock_FindsArticleBody()
    {
        // Arrange - Article body paragraphs scattered among nav/sidebar elements
        // The main content is in a plain div with no special class, but has the largest block of <p> tags
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var articleParagraphs = string.Join("\n", Enumerable.Range(1, 5).Select(i =>
            $"<p>This is article paragraph {i} with substantial text content that exceeds the minimum threshold for extraction.</p>"));
        var html = $@"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <div class='wrapper'>
                    <div class='top-section'>
                        <p>Short nav text here</p>
                    </div>
                    <div class='story-area'>
                        {articleParagraphs}
                    </div>
                    <div class='bottom-section'>
                        <p>Footer text here</p>
                    </div>
                </div>
            </body></html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert
        result.Should().NotBeNull();
        result!.Paragraphs.Count.Should().BeGreaterThanOrEqualTo(3, "largest paragraph block heuristic should find the main article body");
        result.Paragraphs.Should().Contain(p => p.Contains("article paragraph"));
    }

    #endregion

    #region IsArticle

    [Fact]
    public void IsArticle_OgTypeArticleMetaTag_ReturnsTrue()
    {
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html><head><meta property=""og:type"" content=""article"" /></head><body><div>Content</div></body></html>";

        extractor.IsArticle(html).Should().BeTrue("og:type article meta tag is present");
    }

    [Fact]
    public void IsArticle_OgTypeArticleMetaTag_SingleQuotes_ReturnsTrue()
    {
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html><head><meta property='og:type' content='article' /></head><body><div>Content</div></body></html>";

        extractor.IsArticle(html).Should().BeTrue("og:type article meta tag with single quotes is present");
    }

    [Fact]
    public void IsArticle_OgTypeNotArticle_ReturnsFalse()
    {
        // og:type is "website" but the word "article" appears elsewhere on the page
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html><head><meta property=""og:type"" content=""website"" /></head>
            <body><div>Read our latest article about technology</div></body></html>";

        extractor.IsArticle(html).Should().BeFalse("og:type is 'website', not 'article' — the word 'article' in body text should not trigger a match");
    }

    [Fact]
    public void IsArticle_LinkListPage_WithManyShortParagraphs_ReturnsFalse()
    {
        // Index/collection page with many <p> tags but all short (link descriptions)
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html><body>
            <h1>Today's Headlines</h1>
            <p><a href='/story1'>Story 1</a></p>
            <p><a href='/story2'>Story 2</a></p>
            <p><a href='/story3'>Story 3</a></p>
            <p><a href='/story4'>Story 4</a></p>
            <p><a href='/story5'>Story 5</a></p>
        </body></html>";

        extractor.IsArticle(html).Should().BeFalse("short link-list paragraphs should not count as article content");
    }

    [Fact]
    public void IsArticle_ParagraphsInSidebar_NotCounted()
    {
        // Page with substantial <p> tags only in nav/footer/sidebar
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var longText = new string('x', 60);
        var html = $@"<html><body>
            <nav><p>{longText} navigation text here with more content.</p></nav>
            <aside><p>{longText} sidebar promotional text with details.</p></aside>
            <footer><p>{longText} footer disclaimer text and legal info.</p></footer>
            <div>Short main content</div>
        </body></html>";

        // No <article>, <main>, og:type, or article-class markers. The substantial paragraphs
        // are only in boilerplate regions, so this should not be classified as an article.
        extractor.IsArticle(html).Should().BeFalse("substantial paragraphs are only in nav/aside/footer");
    }

    [Fact]
    public void IsArticle_SubstantialParagraphsInMain_ReturnsTrue()
    {
        // Page with substantial <p> tags inside <main>, no other article indicators
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var longText = new string('x', 60);
        var html = $@"<html><body>
            <main>
                <p>{longText} first paragraph of real article content here.</p>
                <p>{longText} second paragraph continues with more details.</p>
                <p>{longText} third paragraph wraps up with conclusions.</p>
            </main>
        </body></html>";

        extractor.IsArticle(html).Should().BeTrue("3 substantial paragraphs inside <main> indicates article content");
    }

    [Fact]
    public void IsArticle_SubstantialParagraphsInArticleElement_ReturnsTrue()
    {
        // <article> tag also triggers the earlier check, but let's ensure substantial
        // paragraphs inside <article> work for the paragraph-counting path too
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html><body><article><h1>Title</h1></article></body></html>";

        // This triggers via the <article> tag check, not the paragraph count
        extractor.IsArticle(html).Should().BeTrue("has <article> tag");
    }

    [Fact]
    public void IsArticle_FalsePositive_OgTypeWebsiteWithArticleWord_ReturnsFalse()
    {
        // Reproduces the original bug: page has og:type "website" and the word "article"
        // appears in a link or text, causing false positive with substring matching
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html>
            <head><meta property=""og:type"" content=""website"" /></head>
            <body>
                <h1>Index of Articles</h1>
                <ul>
                    <li><a href='/article/1'>First article link</a></li>
                    <li><a href='/article/2'>Second article link</a></li>
                </ul>
            </body></html>";

        extractor.IsArticle(html).Should().BeFalse("og:type is 'website' and 'article' only appears in text/links, not as an article indicator");
    }

    [Fact]
    public void IsArticle_ManyEmptyParagraphTags_ReturnsFalse()
    {
        // Page with many <p> tags but they're all empty or trivially short
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var html = @"<html><body>
            <p></p>
            <p>&nbsp;</p>
            <p> </p>
            <p>x</p>
            <p>short</p>
            <p>also short</p>
        </body></html>";

        extractor.IsArticle(html).Should().BeFalse("empty/trivially short paragraphs should not count as article content");
    }

    [Fact]
    public void IsArticle_NavigationArticleClass_ReturnsFalse()
    {
        // class="navigation-article" should NOT match the "article" indicator
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property=""og:type"" content=""website"" /></head>
        <body>
            <nav class=""navigation-article"">
                <a href=""/page1"">Page 1</a>
                <a href=""/page2"">Page 2</a>
            </nav>
            <div class=""article-list"">
                <p>Short desc</p>
                <p>Another</p>
            </div>
        </body></html>";

        extractor.IsArticle(html).Should().BeFalse("hyphenated class names like 'navigation-article' and 'article-list' should not match the 'article' indicator");
    }

    [Fact]
    public void IsArticle_ExactArticleClass_ReturnsTrue()
    {
        // class="article" (exact) should match
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head></head>
        <body>
            <div class=""article"">
                <p>Short content</p>
            </div>
        </body></html>";

        extractor.IsArticle(html).Should().BeTrue("exact class 'article' should match");
    }

    [Fact]
    public void IsArticle_ArticleInMultipleClasses_ReturnsTrue()
    {
        // class="main article sidebar" should match — "article" is exact within space-separated list
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head></head>
        <body>
            <div class=""main article sidebar"">
                <p>Short content</p>
            </div>
        </body></html>";

        extractor.IsArticle(html).Should().BeTrue("'article' as a space-separated class token should match");
    }

    [Fact]
    public void IsArticle_ExactArticleId_ReturnsTrue()
    {
        // id="article" should match
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head></head>
        <body>
            <div id=""article"">
                <p>Short content</p>
            </div>
        </body></html>";

        extractor.IsArticle(html).Should().BeTrue("exact id 'article' should match");
    }

    [Fact]
    public void IsArticle_HyphenatedArticleId_ReturnsFalse()
    {
        // id="article-wrapper" should NOT match "article" indicator
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var html = @"<html><head><meta property=""og:type"" content=""website"" /></head>
        <body>
            <div id=""article-wrapper"">
                <p>Short</p>
            </div>
        </body></html>";

        extractor.IsArticle(html).Should().BeFalse("hyphenated id 'article-wrapper' should not match 'article' indicator");
    }

    #endregion

    #region HasExtractableContent

    [Fact]
    public void HasExtractableContent_ArticleWithSubstantialParagraphs_ReturnsTrue()
    {
        var p1 = "This is the first paragraph of the article with plenty of content to read through carefully.";
        var p2 = "The second paragraph continues with additional details and information for readers to enjoy fully.";
        var p3 = "A third paragraph wraps up the section with concluding thoughts and final remarks for everyone.";
        var html = $@"<html><body>
            <article>
                <h1>Real Article</h1>
                <p>{p1}</p>
                <p>{p2}</p>
                <p>{p3}</p>
            </article>
        </body></html>";

        ReadableContentExtractor.HasExtractableContent(html).Should().BeTrue(
            "article tag with 3+ substantial paragraphs should be extractable");
    }

    [Fact]
    public void HasExtractableContent_JsShellWithArticleIndicators_ReturnsFalse()
    {
        // Simulates a JS-rendered page: has og:type=article but no article paragraphs
        var html = @"<html><head><meta property='og:type' content='article' /></head>
            <body>
                <nav><a href='/'>Home</a><a href='/news'>News</a></nav>
                <div id='root'></div>
                <footer><p>Copyright 2026 Example Corp. All rights reserved. Terms and conditions apply.</p></footer>
            </body></html>";

        ReadableContentExtractor.HasExtractableContent(html).Should().BeFalse(
            "JS shell page with only nav/footer text should NOT have extractable content");
    }

    [Fact]
    public void HasExtractableContent_EmptyString_ReturnsFalse()
    {
        ReadableContentExtractor.HasExtractableContent("").Should().BeFalse();
    }

    [Fact]
    public void HasExtractableContent_MainRoleWithContent_ReturnsTrue()
    {
        var p1 = "This is the first paragraph with enough content to be considered substantial by the extractor.";
        var p2 = "Second paragraph also has sufficient length to pass the threshold for extraction purposes.";
        var p3 = "Third paragraph rounds out the article with concluding thoughts about the topic at hand.";
        var html = $@"<html><body>
            <div role='main'>
                <p>{p1}</p>
                <p>{p2}</p>
                <p>{p3}</p>
            </div>
        </body></html>";

        ReadableContentExtractor.HasExtractableContent(html).Should().BeTrue();
    }

    [Fact]
    public void HasExtractableContent_LargestParagraphBlockInPlainDiv_ReturnsTrue()
    {
        // Content is in a plain div (no special selectors) but has a large block of paragraphs.
        // Each paragraph needs >50 chars, total must be 100+ words (ValidateContentQuality),
        // and paragraphs must not all start with the same word.
        var html = @"<html><body>
            <div class='story-area'>
                <p>First paragraph describes the opening scene where the protagonist arrives at the grand hotel overlooking the coast on a beautiful summer morning with clear skies.</p>
                <p>Meanwhile, the detective examines the evidence found at the crime scene and pieces together the timeline carefully, noting each detail in a small leather notebook.</p>
                <p>Several witnesses reported seeing a dark figure near the entrance shortly before midnight on that fateful evening, though none could identify the individual clearly.</p>
                <p>Forensic analysis revealed traces of an unusual chemical compound on the windowsill that puzzled the entire investigative team and prompted further laboratory testing.</p>
                <p>Ultimately, the resolution came from an unexpected source when a security camera captured the crucial moment on tape, revealing the identity of the perpetrator.</p>
            </div>
        </body></html>";

        ReadableContentExtractor.HasExtractableContent(html).Should().BeTrue(
            "largest paragraph block heuristic should find the content");
    }

    #endregion

    #region IsArticlePage (static)

    [Fact]
    public void IsArticlePage_MatchesInstanceIsArticle()
    {
        // IsArticlePage (static) should produce the same result as IsArticle (instance)
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);

        var articleHtml = @"<html><head><meta property='og:type' content='article' /></head><body></body></html>";
        var nonArticleHtml = @"<html><body><p>Short text</p></body></html>";

        ReadableContentExtractor.IsArticlePage(articleHtml).Should().Be(extractor.IsArticle(articleHtml));
        ReadableContentExtractor.IsArticlePage(nonArticleHtml).Should().Be(extractor.IsArticle(nonArticleHtml));
    }

    [Fact]
    public void IsArticlePage_OgTypeArticle_ReturnsTrue()
    {
        var html = @"<html><head><meta property='og:type' content='article' /></head><body></body></html>";
        ReadableContentExtractor.IsArticlePage(html).Should().BeTrue();
    }

    [Fact]
    public void IsArticlePage_NoArticleIndicators_ReturnsFalse()
    {
        var html = @"<html><body><div>Just a page</div></body></html>";
        ReadableContentExtractor.IsArticlePage(html).Should().BeFalse();
    }

    #endregion

    #region Title Extraction — Navigation Text Rejection

    [Fact]
    public async Task ExtractAsync_NytLikeHtml_UsesArticleHeadlineNotNavigationH1()
    {
        // Arrange — simulates NYT structure: navigation H1 "Today's Paper" before article H1
        var html = @"
            <html>
            <head>
                <meta property=""og:title"" content=""The Real Article Headline"" />
                <meta property=""article:published_time"" content=""2026-03-23"" />
            </head>
            <body>
                <header>
                    <h1>Today's Paper</h1>
                </header>
                <article>
                    <h1 class=""headline"">The Real Article Headline</h1>
                    <p>This is a sufficiently long paragraph to pass content extraction thresholds and quality gates. It needs to be long enough so the content extractor does not reject it as too short. Adding more text here to ensure the paragraph count and word count requirements are met.</p>
                    <p>Second paragraph with additional content to satisfy extraction requirements.</p>
                </article>
            </body>
            </html>";

        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        // Act
        var result = await extractor.ExtractAsync(html, "https://www.nytimes.com/2026/03/23/article.html");

        // Assert
        result.Should().NotBeNull();
        result!.Title.Should().Be("The Real Article Headline");
    }

    [Fact]
    public async Task ExtractAsync_GenericH1IsNavigationText_FallsBackToOgTitle()
    {
        // Arrange — page where only H1 is navigation text and og:title has the real headline
        var html = @"
            <html>
            <head>
                <meta property=""og:title"" content=""Actual Article Title"" />
                <meta property=""article:published_time"" content=""2026-03-23"" />
            </head>
            <body>
                <h1>Home</h1>
                <div class=""article-body"">
                    <p>This is a sufficiently long paragraph to pass content extraction thresholds. Adding more text to ensure word count requirements are met for the quality gate.</p>
                    <p>Second paragraph with additional content for extraction.</p>
                </div>
            </body>
            </html>";

        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert
        result.Should().NotBeNull();
        result!.Title.Should().Be("Actual Article Title");
    }

    [Fact]
    public async Task ExtractAsync_OgTitleIsNavigationText_SkipsToNextSource()
    {
        // Arrange — og:title is "Today's Paper" (navigation text), should fall through to <title> tag
        var html = @"
            <html>
            <head>
                <meta property=""og:title"" content=""Today's Paper"" />
                <title>The New York Times - Breaking News</title>
            </head>
            <body>
                <article>
                    <p>This is a sufficiently long paragraph to pass content extraction thresholds and quality gates. It needs to be long enough so the content extractor does not reject it as too short.</p>
                    <p>Second paragraph with additional content to satisfy extraction requirements and word count minimums for the quality validation step.</p>
                </article>
            </body>
            </html>";

        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        // Act
        var result = await extractor.ExtractAsync(html, "https://www.nytimes.com/section/todayspaper");

        // Assert — should NOT use "Today's Paper" as the title
        result.Should().NotBeNull();
        result!.Title.Should().NotBe("Today's Paper");
        result.Title.Should().Contain("New York Times");
    }

    #endregion

    #region Boilerplate pass spares the document skeleton (workspace-79zhv)

    private const string SkeletonP1 = "The first paragraph of the story is comfortably longer than fifty characters so it counts as real article text.";
    private const string SkeletonP2 = "The second paragraph carries on with more reporting detail, again well past the fifty character threshold.";
    private const string SkeletonP3 = "A third paragraph closes the section with concluding remarks that are also longer than fifty characters.";

    private static string SkeletonArticleBody(string wrapperClass) => $@"
        <div class='{wrapperClass}'>
            <h1>Council Approves New Transit Plan</h1>
            <p>{SkeletonP1}</p>
            <p>{SkeletonP2}</p>
            <p>{SkeletonP3}</p>
        </div>";

    // Guards:   A page captured while a modal/consent/paywall is up (state class on <body>) still
    //           yields its article instead of an empty reader view.
    // Fails if: the boilerplate pass removes <body> because its class contains 'modal'/'overlay'/
    //           'cookie'/'paywall'/'newsletter', or the paragraph walk treats <body> as boilerplate.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ExtractAsync_BodyStateClass_StillExtractsArticle"
    [Theory]
    [InlineData("modal-open")]
    [InlineData("has-overlay")]
    [InlineData("paywall-active")]
    [InlineData("cookie-consent-shown")]
    [InlineData("newsletter-modal-visible")]
    public async Task ExtractAsync_BodyStateClass_StillExtractsArticle(string bodyClass)
    {
        var html = $@"<html><head><title>Council Approves New Transit Plan</title>
            <meta property='og:type' content='article' /></head>
            <body class='{bodyClass}'>{SkeletonArticleBody("article-body")}</body></html>";
        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        result.Should().NotBeNull();
        result!.Paragraphs.Should().Contain(p => p.Contains("first paragraph of the story"));
        result.Paragraphs.Should().Contain(p => p.Contains("second paragraph carries on"));
        result.Paragraphs.Should().Contain(p => p.Contains("third paragraph closes"));
    }

    // Guards:   Same as above but with the state class on <html> (e.g. paywall-active) rather than <body>.
    // Fails if: the boilerplate pass removes <html>, taking the whole document with it.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ExtractAsync_HtmlStateClass_StillExtractsArticle"
    [Theory]
    [InlineData("paywall-active")]
    [InlineData("modal-open overlay-visible")]
    public async Task ExtractAsync_HtmlStateClass_StillExtractsArticle(string htmlClass)
    {
        var html = $@"<html class='{htmlClass}'><head><title>Council Approves New Transit Plan</title>
            <meta property='og:type' content='article' /></head>
            <body>{SkeletonArticleBody("article-body")}</body></html>";
        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        result.Should().NotBeNull();
        result!.Paragraphs.Should().Contain(p => p.Contains("first paragraph of the story"));
        result.Paragraphs.Should().Contain(p => p.Contains("third paragraph closes"));
    }

    // Guards:   The no-known-container fallback (full-document paragraph walk) is not defeated by a
    //           <body class="modal-open"> ancestor being mistaken for a boilerplate region.
    // Fails if: IsInsideBoilerplate counts the 'modal' class on <body>/<html> against every paragraph.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ExtractAsync_BodyStateClass_NoContentContainer_StillExtractsArticle"
    [Fact]
    public async Task ExtractAsync_BodyStateClass_NoContentContainer_StillExtractsArticle()
    {
        var html = $@"<html class='has-overlay'><head><title>Council Approves New Transit Plan</title></head>
            <body class='modal-open'>{SkeletonArticleBody("wrapper")}</body></html>";
        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        var result = await extractor.ExtractAsync(html, "https://example.com/story");

        result.Should().NotBeNull();
        result!.Paragraphs.Should().Contain(p => p.Contains("first paragraph of the story"));
        result.Paragraphs.Should().Contain(p => p.Contains("second paragraph carries on"));
    }

    // Guards:   The HasExtractableContent pre-check (same boilerplate pass) also keeps a page whose
    //           <body> carries a modal state class.
    // Fails if: RemoveBoilerplate deletes <body> so the pre-check says there is nothing to read.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~HasExtractableContent_BodyStateClass_ReturnsTrue"
    [Fact]
    public void HasExtractableContent_BodyStateClass_ReturnsTrue()
    {
        var html = $@"<html><head><title>Council Approves New Transit Plan</title></head>
            <body class='modal-open paywall-active'>{SkeletonArticleBody("article-body")}</body></html>";

        ReadableContentExtractor.HasExtractableContent(html).Should().BeTrue();
    }

    // Guards:   Real boilerplate inside the body (a cookie banner, a modal div) is still stripped
    //           out of the article text; the fix only spares <html>/<head>/<body>.
    // Fails if: the skeleton guard is widened so ordinary elements with boilerplate classes survive.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ExtractAsync_CookieBannerInsideBody_StillStripped"
    [Fact]
    public async Task ExtractAsync_CookieBannerInsideBody_StillStripped()
    {
        var html = $@"<html><head><title>Council Approves New Transit Plan</title>
            <meta property='og:type' content='article' /></head>
            <body class='modal-open'>
                <div class='cookie-banner'><p>Accept cookies so we can personalise the advertising you see on this website today.</p></div>
                <div class='newsletter-modal'><p>Sign up to our newsletter for the very latest headlines delivered every morning.</p></div>
                {SkeletonArticleBody("article-body")}
            </body></html>";
        var extractor = new ReadableContentExtractor(Substitute.For<ILogger<ReadableContentExtractor>>());

        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        result.Should().NotBeNull();
        result!.Paragraphs.Should().Contain(p => p.Contains("first paragraph of the story"));
        result.Paragraphs.Should().NotContain(p => p.Contains("Accept cookies"));
        result.Paragraphs.Should().NotContain(p => p.Contains("Sign up to our newsletter"));
    }

    #endregion
}
