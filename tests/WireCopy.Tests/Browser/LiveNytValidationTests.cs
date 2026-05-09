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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SectionPageMetadataGuard_MultipleArticles_ShouldNotShareSameAuthor()
    {
        Skip.IfNot(File.Exists(HtmlPath), $"NYT section fixture missing: {HtmlPath}");

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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task InlineVsHeadlineClassification_HeadlinesInH2H3_ShouldBeContent()
    {
        Skip.IfNot(File.Exists(HtmlPath), $"NYT section fixture missing: {HtmlPath}");

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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task LinkCount_ShouldHaveReasonableNumberOfContentLinks()
    {
        Skip.IfNot(File.Exists(HtmlPath), $"NYT section fixture missing: {HtmlPath}");

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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NoInlineTextFragmentsDominating_NavigationShouldNotDwarfContent()
    {
        Skip.IfNot(File.Exists(HtmlPath), $"NYT section fixture missing: {HtmlPath}");

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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldExtractTitle()
    {
        Skip.IfNot(File.Exists(BbcHtmlPath), $"BBC fixture missing: {BbcHtmlPath}");

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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldExtractMultipleParagraphs()
    {
        Skip.IfNot(File.Exists(BbcHtmlPath), $"BBC fixture missing: {BbcHtmlPath}");

        // Arrange
        var html = File.ReadAllText(BbcHtmlPath);

        // Act
        var content = await _contentExtractor.ExtractAsync(html, "https://www.bbc.com/news/articles/cvg5yp7v0ppo");

        // Assert
        content.Should().NotBeNull("BBC article should be recognized as readable content");
        content!.Paragraphs.Should().HaveCountGreaterOrEqualTo(10,
            "a substantial BBC news article should have at least 10 content paragraphs");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldContainArticleTextNotBoilerplate()
    {
        Skip.IfNot(File.Exists(BbcHtmlPath), $"BBC fixture missing: {BbcHtmlPath}");

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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BbcArticle_ReaderView_ShouldHaveReasonableContentLength()
    {
        Skip.IfNot(File.Exists(BbcHtmlPath), $"BBC fixture missing: {BbcHtmlPath}");

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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldExtractTitle()
    {
        Skip.IfNot(File.Exists(NytArticleHtmlPath), $"NYT article fixture missing: {NytArticleHtmlPath}");

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.Title.Should().NotBeNullOrWhiteSpace("title should be extracted from the article");
        content.Title.Should().Contain("German",
            "the article title is about German industry and energy costs");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldExtractAuthor()
    {
        Skip.IfNot(File.Exists(NytArticleHtmlPath), $"NYT article fixture missing: {NytArticleHtmlPath}");

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.Author.Should().NotBeNullOrWhiteSpace("author should be extracted");
        content.Author.Should().Contain("Nelson",
            "the article is by Eshe Nelson");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldExtractAtLeastFiveParagraphs()
    {
        Skip.IfNot(File.Exists(NytArticleHtmlPath), $"NYT article fixture missing: {NytArticleHtmlPath}");

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.Paragraphs.Should().HaveCountGreaterOrEqualTo(5,
            "a substantial NYT news article should have at least 5 content paragraphs");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldContainArticleTextNotBoilerplate()
    {
        Skip.IfNot(File.Exists(NytArticleHtmlPath), $"NYT article fixture missing: {NytArticleHtmlPath}");

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");

        // Content should reference article topics, not navigation/boilerplate
        content!.CleanedText.Should().ContainAny("Germany", "energy", "factory", "factories", "industrial",
            "article text should reference the core topic about German energy costs");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ShouldHaveSubstantialContent()
    {
        Skip.IfNot(File.Exists(NytArticleHtmlPath), $"NYT article fixture missing: {NytArticleHtmlPath}");

        var html = File.ReadAllText(NytArticleHtmlPath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/03/13/business/energy-environment/iran-energy-costs-germany-factories.html");

        content.Should().NotBeNull("NYT article should be recognized as readable content");
        content!.CleanedText.Length.Should().BeGreaterThan(1000,
            "extracted NYT article content should be substantial, not a truncated snippet");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NytArticle_ReaderView_ContentShouldNotBeFromRedirectOrSectionPage()
    {
        Skip.IfNot(File.Exists(NytArticleHtmlPath), $"NYT article fixture missing: {NytArticleHtmlPath}");

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

    #region NYT 2026 Pinned Regression (workspace-d799)

    /// <summary>
    /// Pinned fixture committed to the repo (Fixtures/nyt/nyt-2026-article.html) so this
    /// regression cannot silently disappear when /workspace/screenshots/ goes missing.
    /// </summary>
    private static readonly string Nyt2026FixturePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "nyt", "nyt-2026-article.html");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadableContentExtractor_WithNyt2026Markup_ExtractsArticleBody()
    {
        // workspace-d799: NYT 2026 ships hashed CSS-module class names; only the
        // <section name="articleBody"> shell is stable. The new selector ordering
        // must pick this up without falling through to density extraction.
        File.Exists(Nyt2026FixturePath).Should().BeTrue(
            $"committed regression fixture must be present at {Nyt2026FixturePath}");

        var html = await File.ReadAllTextAsync(Nyt2026FixturePath);

        var content = await _contentExtractor.ExtractAsync(
            html, "https://www.nytimes.com/2026/04/29/business/energy-environment/germany-factory-energy.html");

        content.Should().NotBeNull("NYT 2026 article markup must produce readable content");
        content!.Paragraphs.Should().HaveCountGreaterOrEqualTo(4,
            "the fixture has 8 paragraphs in <section name=articleBody>; selectors should pick them all up");
        content.WordCount.Should().BeGreaterOrEqualTo(200,
            "the fixture body is roughly 500 words; extraction should preserve at least 200");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateContentQuality_LongFormAlphabeticBypass_AcceptsArticle()
    {
        // workspace-d799: long, paragraph-rich content should self-validate even when
        // the alphabetic-character ratio falls under the 0.70 gate (curly quotes,
        // em-dashes, numerals push it down on real NYT bodies).
        var paragraphs = BuildLowAlphabeticRatioFixture(targetWords: 600, paragraphs: 10, alphabeticRatio: 0.65);

        // Sanity check the synthesised fixture matches the conditions named in the bead.
        var totalWords = paragraphs.Sum(p => p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        totalWords.Should().BeGreaterOrEqualTo(400,
            "long-form bypass requires totalWords >= 400");
        paragraphs.Should().HaveCountGreaterOrEqualTo(8,
            "long-form bypass requires paragraphs >= 8");

        var totalChars = paragraphs.Sum(p => p.Length);
        var alphaChars = paragraphs.Sum(p => p.Count(char.IsLetter));
        var ratio = (double)alphaChars / totalChars;
        ratio.Should().BeLessThan(0.70,
            "fixture must trip the legacy alphabetic-ratio gate, otherwise the test proves nothing");

        ReadableContentExtractor.ValidateContentQuality(paragraphs).Should().BeTrue(
            "long-form, paragraph-rich content must bypass the alphabetic-ratio and avg-length gates");
    }

    /// <summary>
    /// Builds a paragraph list with the requested rough word count, paragraph count,
    /// and target alphabetic ratio. The non-alphabetic mass comes from numerals/
    /// punctuation so the content still parses as paragraphs.
    /// </summary>
    private static List<string> BuildLowAlphabeticRatioFixture(int targetWords, int paragraphs, double alphabeticRatio)
    {
        var wordsPerParagraph = targetWords / paragraphs;
        var result = new List<string>(paragraphs);
        // Vary the leading word per paragraph so we don't trip the
        // "repeated first word > 50%" gate, which is independent of the bypass
        // and validates real article-shaped variation.
        string[] leads =
        {
            "The factory", "Workers at", "Across Germany", "Economists warn",
            "By April", "In Mannheim", "Coalition partners", "Industry leaders",
            "Meanwhile, output", "For decades",
        };

        for (var i = 0; i < paragraphs; i++)
        {
            var lead = leads[i % leads.Length];
            var alpha = $"{lead} cooled to ambient as the third furnace was throttled back overnight while plant operators reviewed export contracts and energy hedging arrangements";

            var sb = new System.Text.StringBuilder();
            var built = 0;
            var sentenceWordCount = alpha.Split(' ').Length;
            while (built < wordsPerParagraph)
            {
                sb.Append(alpha).Append(' ');
                built += sentenceWordCount;
            }

            var alphaChunk = sb.ToString().TrimEnd();
            // Append a non-alphabetic tail sized to drag the ratio down to the target.
            // ratio = alphaChars / (alphaChars + nonAlphaChars). Solve nonAlphaChars.
            var alphaCharCount = alphaChunk.Count(char.IsLetter);
            var totalNeeded = (int)(alphaCharCount / alphabeticRatio);
            var nonAlphaNeeded = Math.Max(0, totalNeeded - alphaChunk.Length);
            var tail = string.Concat(Enumerable.Repeat("0123456789 — ", (nonAlphaNeeded / 13) + 1)).Substring(0, nonAlphaNeeded);
            result.Add(alphaChunk + " " + tail);
        }

        return result;
    }

    #endregion
}
