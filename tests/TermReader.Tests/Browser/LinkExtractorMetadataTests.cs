// Educational and personal use only.

using FluentAssertions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkExtractorMetadataTests
{
    [Fact]
    public void ExtractLinkMetadata_TimeTag_ReturnsDate()
    {
        var html = @"
            <div>
                <time datetime=""2024-03-15T10:30:00Z"">March 15, 2024</time>
                <a href=""https://example.com/article"">Article Title</a>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, pubDate) = LinkExtractor.ExtractLinkMetadata(anchor);

        pubDate.Should().NotBeNull();
        pubDate!.Value.Year.Should().Be(2024);
        pubDate.Value.Month.Should().Be(3);
        pubDate.Value.Day.Should().Be(15);
    }

    [Fact]
    public void ExtractLinkMetadata_AuthorClass_ReturnsAuthor()
    {
        var html = @"
            <article>
                <span class=""author"">Jane Doe</span>
                <a href=""https://example.com/article"">Article Title</a>
            </article>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, _) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().Be("Jane Doe");
    }

    [Fact]
    public void ExtractLinkMetadata_BylineClass_ReturnsAuthor()
    {
        var html = @"
            <div>
                <p class=""byline"">By John Smith</p>
                <a href=""https://example.com/article"">Article Title</a>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, _) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().Be("By John Smith");
    }

    [Fact]
    public void ExtractLinkMetadata_RelAuthor_ReturnsAuthor()
    {
        var html = @"
            <article>
                <a rel=""author"" href=""/profile/jd"">Jane Doe</a>
                <a href=""https://example.com/article"">Article Title</a>
            </article>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectNodes("//a")[1]; // Second anchor

        var (author, _) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().Be("Jane Doe");
    }

    [Fact]
    public void ExtractLinkMetadata_NoMetadata_ReturnsNulls()
    {
        var html = @"
            <div>
                <a href=""https://example.com"">Plain Link</a>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, pubDate) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().BeNull();
        pubDate.Should().BeNull();
    }

    [Fact]
    public void ExtractLinkMetadata_NoContainer_ReturnsNulls()
    {
        var html = @"<a href=""https://example.com"">Bare Link</a>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, pubDate) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().BeNull();
        pubDate.Should().BeNull();
    }

    [Fact]
    public void ExtractLinkMetadata_DateTimeOffset_ParsedCorrectly()
    {
        var html = @"
            <section>
                <time datetime=""2024-12-25T08:00:00-05:00"">Dec 25</time>
                <a href=""https://example.com/holiday"">Holiday Article</a>
            </section>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (_, pubDate) = LinkExtractor.ExtractLinkMetadata(anchor);

        pubDate.Should().NotBeNull();
        pubDate!.Value.Month.Should().Be(12);
        pubDate.Value.Day.Should().Be(25);
    }

    [Fact]
    public void ExtractLinkMetadata_BothAuthorAndDate_ReturnsBoth()
    {
        var html = @"
            <article>
                <span class=""author"">Alice</span>
                <time datetime=""2024-06-15"">June 15</time>
                <a href=""https://example.com/story"">Story Title</a>
            </article>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, pubDate) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().Be("Alice");
        pubDate.Should().NotBeNull();
        pubDate!.Value.Month.Should().Be(6);
    }

    #region NYT-style "By" prefix author extraction

    [Fact]
    public void ExtractLinkMetadata_ByPrefixSpan_ExtractsAuthor()
    {
        var html = @"
            <article>
                <span>By Jane Doe</span>
                <a href=""https://example.com/article"">Article Title</a>
            </article>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, _) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().Be("Jane Doe");
    }

    [Fact]
    public void ExtractLinkMetadata_ByPrefixParagraph_ExtractsAuthor()
    {
        var html = @"
            <div>
                <p>By Alice and Bob</p>
                <a href=""https://example.com/article"">Article Title</a>
            </div>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var anchor = doc.DocumentNode.SelectSingleNode("//a");

        var (author, _) = LinkExtractor.ExtractLinkMetadata(anchor);

        author.Should().Be("Alice and Bob");
    }

    #endregion

    #region Page-level JSON-LD metadata

    [Fact]
    public void ExtractPageLevelMetadata_JsonLdAuthorObject_ReturnsAuthor()
    {
        var html = @"<html><head>
            <script type='application/ld+json'>
            {""@type"":""NewsArticle"",""author"":{""@type"":""Person"",""name"":""John Smith""}}
            </script>
        </head><body></body></html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var (author, _) = LinkExtractor.ExtractPageLevelMetadata(doc);

        author.Should().Be("John Smith");
    }

    [Fact]
    public void ExtractPageLevelMetadata_JsonLdAuthorArray_ReturnsCommaJoined()
    {
        var html = @"<html><head>
            <script type='application/ld+json'>
            {""@type"":""Article"",""author"":[{""name"":""Alice""},{""name"":""Bob""}]}
            </script>
        </head><body></body></html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var (author, _) = LinkExtractor.ExtractPageLevelMetadata(doc);

        author.Should().Be("Alice, Bob");
    }

    [Fact]
    public void ExtractPageLevelMetadata_JsonLdAuthorString_ReturnsAuthor()
    {
        var html = @"<html><head>
            <script type='application/ld+json'>
            {""@type"":""Article"",""author"":""Sarah Connor""}
            </script>
        </head><body></body></html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var (author, _) = LinkExtractor.ExtractPageLevelMetadata(doc);

        author.Should().Be("Sarah Connor");
    }

    #endregion

    #region Page-level meta date extraction

    [Fact]
    public void ExtractPageLevelMetadata_ArticlePublishedTime_ReturnsDate()
    {
        var html = @"<html><head>
            <meta property='article:published_time' content='2024-06-15T10:00:00Z' />
        </head><body></body></html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var (_, pubDate) = LinkExtractor.ExtractPageLevelMetadata(doc);

        pubDate.Should().NotBeNull();
        pubDate!.Value.Year.Should().Be(2024);
        pubDate.Value.Month.Should().Be(6);
        pubDate.Value.Day.Should().Be(15);
    }

    [Fact]
    public void ExtractPageLevelMetadata_MetaNameAuthor_ReturnsAuthor()
    {
        var html = @"<html><head>
            <meta name='author' content='Page Author' />
        </head><body></body></html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var (author, _) = LinkExtractor.ExtractPageLevelMetadata(doc);

        author.Should().Be("Page Author");
    }

    [Fact]
    public void ExtractPageLevelMetadata_MetaAuthorUrl_IgnoresUrls()
    {
        var html = @"<html><head>
            <meta name='author' content='https://example.com/author' />
        </head><body></body></html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var (author, _) = LinkExtractor.ExtractPageLevelMetadata(doc);

        author.Should().BeNull("URL-style authors should be ignored");
    }

    [Fact]
    public void ExtractPageLevelMetadata_NoMetadata_ReturnsNulls()
    {
        var html = @"<html><head></head><body></body></html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var (author, pubDate) = LinkExtractor.ExtractPageLevelMetadata(doc);

        author.Should().BeNull();
        pubDate.Should().BeNull();
    }

    #endregion

    #region Page-level fallback integration

    [Fact]
    public async Task ExtractLinksAsync_PageLevelAuthor_AppliedToContentLinks()
    {
        var logger = Substitute.For<ILogger<LinkExtractor>>();
        var extractor = new LinkExtractor(logger);

        var html = @"<html><head>
            <script type='application/ld+json'>
            {""@type"":""Article"",""author"":{""name"":""Page Author""}}
            </script>
        </head><body>
            <article>
                <a href=""https://example.com/story"">A Very Long Story Title That Exceeds Thirty Characters</a>
            </article>
        </body></html>";

        var links = await extractor.ExtractLinksAsync(html, "https://example.com");

        links.Should().ContainSingle();
        links[0].Author.Should().Be("Page Author");
    }

    [Fact]
    public async Task ExtractLinksAsync_PerLinkAuthor_TakesPrecedenceOverPageLevel()
    {
        var logger = Substitute.For<ILogger<LinkExtractor>>();
        var extractor = new LinkExtractor(logger);

        var html = @"<html><head>
            <script type='application/ld+json'>
            {""@type"":""Article"",""author"":{""name"":""Page Author""}}
            </script>
        </head><body>
            <article>
                <span class=""author"">Link Author</span>
                <a href=""https://example.com/story"">A Very Long Story Title That Exceeds Thirty Characters</a>
            </article>
        </body></html>";

        var links = await extractor.ExtractLinksAsync(html, "https://example.com");

        links.Should().ContainSingle();
        links[0].Author.Should().Be("Link Author", "per-link metadata takes precedence");
    }

    #endregion

    [Fact]
    public void GroupLinksByUrl_PropagatesMetadataFromBestRepresentative()
    {
        var links = new List<TermReader.Domain.ValueObjects.Browser.LinkInfo>
        {
            new()
            {
                Url = "https://example.com/article",
                DisplayText = "Short",
                Type = TermReader.Domain.Enums.Browser.LinkType.Content,
                ImportanceScore = 50,
                Author = "Jane Doe",
                PublishedDate = new DateTime(2024, 3, 15)
            },
            new()
            {
                Url = "https://example.com/article",
                DisplayText = "Much Longer Article Title Here",
                Type = TermReader.Domain.Enums.Browser.LinkType.Content,
                ImportanceScore = 70
            }
        };

        var result = LinkExtractor.GroupLinksByUrl(links);

        result.Should().HaveCount(1);
        result[0].Author.Should().Be("Jane Doe");
        result[0].PublishedDate.Should().NotBeNull();
    }
}
