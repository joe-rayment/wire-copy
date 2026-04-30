// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Integration tests that validate LinkExtractor behavior against real NYT
/// Today's Paper section HTML, ensuring key classification fixes hold.
/// </summary>
[Trait("Category", "Integration")]
public class LiveNytValidationTests
{
    private readonly LinkExtractor _extractor;
    private readonly ReadableContentExtractor _contentExtractor;

    private static readonly string HtmlPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "screenshots", "nyt-todayspaper.html");

    private static readonly string BbcHtmlPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "screenshots", "bbc-article.html");

    private static readonly string NytArticleHtmlPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "screenshots", "nyt-iran-energy-article.html");

    public LiveNytValidationTests()
    {
        var logger = Substitute.For<ILogger<LinkExtractor>>();
        _extractor = new LinkExtractor(logger);

        var contentLogger = Substitute.For<ILogger<ReadableContentExtractor>>();
        _contentExtractor = new ReadableContentExtractor(contentLogger);
    }

    #region Live NYT Section Page Validation

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SectionPageMetadataGuard_MultipleArticles_ShouldNotShareSameAuthor()
    {
        // Skip if fixture not available (xUnit v2 does not support Skip.If)
        if (!File.Exists(HtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(HtmlPath);

        // Act
        var links = await _extractor.ExtractLinksAsync(html, "https://www.nytimes.com/section/todayspaper");

        // Assert - On a section page with many <article> containers,
        // page-level metadata should NOT be blindly applied to all links.
        // If the guard works, content links should have diverse (or null) authors,
        // not all sharing the same single author string.
        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        contentLinks.Should().NotBeEmpty("a section page should have content links");

        var authorsOnContentLinks = contentLinks
            .Where(l => l.Author != null)
            .Select(l => l.Author)
            .ToList();

        if (authorsOnContentLinks.Count > 1)
        {
            // If multiple content links have authors, they should not ALL be the same
            var distinctAuthors = authorsOnContentLinks.Distinct().ToList();
            distinctAuthors.Should().HaveCountGreaterThan(1,
                "section page with many articles should not stamp all links with a single page-level author");
        }

        // Even if no authors are set at all, that's acceptable (guard prevented bad data).
        // The failure mode is: all ~87 links sharing the same author from page metadata.
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InlineVsHeadlineClassification_HeadlinesInH2H3_ShouldBeContent()
    {
        if (!File.Exists(HtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(HtmlPath);

        // Act
        var links = await _extractor.ExtractLinksAsync(html, "https://www.nytimes.com/section/todayspaper");

        // Assert - Article headlines (links inside <h2>, <h3> heading elements)
        // should be classified as Content, not Navigation.
        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        var navigationLinks = links.Where(l => l.Type == LinkType.Navigation).ToList();

        // The NYT Today's Paper page has article headlines in heading tags;
        // these should be classified as Content.
        contentLinks.Should().HaveCountGreaterOrEqualTo(20,
            "NYT Today's Paper should have many content links from article headlines");

        // Navigation links should NOT outnumber Content links on a section page
        // dominated by article headlines.
        navigationLinks.Count.Should().BeLessThanOrEqualTo(contentLinks.Count,
            "on a section page, article headlines should not be misclassified as navigation");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LinkCount_ShouldHaveReasonableNumberOfContentLinks()
    {
        if (!File.Exists(HtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(HtmlPath);

        // Act
        var links = await _extractor.ExtractLinksAsync(html, "https://www.nytimes.com/section/todayspaper");

        // Assert - The NYT Today's Paper page has ~87 article links.
        // We expect a reasonable count of content links.
        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();

        contentLinks.Should().HaveCountGreaterOrEqualTo(40,
            "NYT Today's Paper has many articles; at least 40 should be classified as Content");
        contentLinks.Should().HaveCountLessOrEqualTo(200,
            "content link count should be bounded; excessive count suggests misclassification");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NoInlineTextFragmentsDominating_NavigationShouldNotDwarfContent()
    {
        if (!File.Exists(HtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(HtmlPath);

        // Act
        var links = await _extractor.ExtractLinksAsync(html, "https://www.nytimes.com/section/todayspaper");

        // Assert - Navigation links should not outnumber Content links by a huge ratio
        // on a section page. If inline text fragments are being misclassified as
        // Navigation, the ratio would be skewed heavily toward Navigation.
        var contentCount = links.Count(l => l.Type == LinkType.Content);
        var navigationCount = links.Count(l => l.Type == LinkType.Navigation);

        contentCount.Should().BeGreaterThan(0, "section page must have content links");

        if (navigationCount > 0)
        {
            var ratio = (double)navigationCount / contentCount;
            ratio.Should().BeLessThan(3.0,
                "navigation links should not outnumber content links by more than 3x on a section page");
        }
    }

    #endregion

    #region Live Reader View Validation

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldExtractTitle()
    {
        // Skip if fixture not available
        if (!File.Exists(BbcHtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(BbcHtmlPath);

        // Act
        var content = await _contentExtractor.ExtractAsync(html, "https://www.bbc.com/news/articles/cvg5yp7v0ppo");

        // Assert
        content.Should().NotBeNull("BBC article should be recognized as readable content");
        content!.Title.Should().NotBeNullOrWhiteSpace("title should be extracted from the article");
        content.Title.Should().ContainAny("Netanyahu", "Iran",
            "the article is about Iran regime change and Netanyahu");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldExtractMultipleParagraphs()
    {
        // Skip if fixture not available
        if (!File.Exists(BbcHtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(BbcHtmlPath);

        // Act
        var content = await _contentExtractor.ExtractAsync(html, "https://www.bbc.com/news/articles/cvg5yp7v0ppo");

        // Assert
        content.Should().NotBeNull("BBC article should be recognized as readable content");
        content!.Paragraphs.Should().HaveCountGreaterOrEqualTo(10,
            "a substantial BBC news article should have at least 10 content paragraphs");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldContainArticleTextNotBoilerplate()
    {
        // Skip if fixture not available
        if (!File.Exists(BbcHtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(BbcHtmlPath);

        // Act
        var content = await _contentExtractor.ExtractAsync(html, "https://www.bbc.com/news/articles/cvg5yp7v0ppo");

        // Assert - First paragraph should be actual article text about the topic,
        // not navigation or boilerplate content.
        content.Should().NotBeNull("BBC article should be recognized as readable content");
        content!.Paragraphs.First().Should().ContainAny("Israel", "Iran", "Middle East",
            "first paragraph should reference the article's core topic, not navigation boilerplate");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldHaveReasonableContentLength()
    {
        // Skip if fixture not available
        if (!File.Exists(BbcHtmlPath))
            return;

        // Arrange
        var html = File.ReadAllText(BbcHtmlPath);

        // Act
        var content = await _contentExtractor.ExtractAsync(html, "https://www.bbc.com/news/articles/cvg5yp7v0ppo");

        // Assert
        content.Should().NotBeNull("BBC article should be recognized as readable content");
        content!.CleanedText.Length.Should().BeGreaterThan(500,
            "extracted article content should be substantial, not a truncated snippet");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PageCreate_WithBbcUrl_ShouldPreserveUrl()
    {
        // Arrange
        var url = "https://www.bbc.com/news/articles/cvg5yp7v0ppo";
        var metadata = new PageMetadata { Title = "Test Article" };

        // Act
        var page = Page.Create(url, "<html><body>test</body></html>", metadata);

        // Assert
        page.Url.Should().Be(url, "Page.Create should preserve the URL exactly as provided");
    }

    #endregion

    #region Live NYT Article Reader View Validation

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldExtractTitle()
    {
        if (!File.Exists(NytArticleHtmlPath))
            return;

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.Title.Should().NotBeNullOrWhiteSpace("title should be extracted from the article");
        content.Title.Should().Contain("German",
            "the article title is about German industry and energy costs");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldExtractAuthor()
    {
        if (!File.Exists(NytArticleHtmlPath))
            return;

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.Author.Should().NotBeNullOrWhiteSpace("author should be extracted");
        content.Author.Should().Contain("Nelson",
            "the article is by Eshe Nelson");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldExtractAtLeastFiveParagraphs()
    {
        if (!File.Exists(NytArticleHtmlPath))
            return;

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.Paragraphs.Should().HaveCountGreaterOrEqualTo(5,
            "a substantial NYT news article should have at least 5 content paragraphs");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldContainArticleTextNotBoilerplate()
    {
        if (!File.Exists(NytArticleHtmlPath))
            return;

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");

        // Content should reference article topics, not navigation/boilerplate
        content!.CleanedText.Should().ContainAny("Germany", "energy", "factory", "factories", "industrial",
            "article text should reference the core topic about German energy costs");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldHaveSubstantialContent()
    {
        if (!File.Exists(NytArticleHtmlPath))
            return;

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.CleanedText.Length.Should().BeGreaterThan(1000,
            "extracted NYT article content should be substantial, not a truncated snippet");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ContentShouldNotBeFromRedirectOrSectionPage()
    {
        if (!File.Exists(NytArticleHtmlPath))
            return;

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");

        // Should NOT contain section page indicators
        content!.CleanedText.Should().NotContain("Today's Paper",
            "content should be from the article, not a section page");

        // Should contain coherent article prose, not a list of headlines
        var paragraphs = content.Paragraphs.Where(p => p.Length > 50).ToList();
        paragraphs.Should().HaveCountGreaterOrEqualTo(3,
            "article should have multiple substantial paragraphs, not just headline fragments");
    }

    #endregion
}
