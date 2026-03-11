// Educational and personal use only.

using FluentAssertions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for paywall detection in ReadableContentExtractor.
/// DetectPaywall is internal static and TermReader.Tests has InternalsVisibleTo access.
/// </summary>
[Trait("Category", "Unit")]
public class PaywallDetectionTests
{
    [Fact]
    public void DetectPaywall_ReturnsTrue_WhenPaywallElementsAndFewParagraphs()
    {
        // Arrange - NYT-style gateway class with fewer than 5 paragraphs
        var html = @"
            <html>
            <body>
                <article>
                    <p>First paragraph of the article with some content.</p>
                    <p>Second paragraph continues the story.</p>
                </article>
                <div class='gateway-content'>
                    <h2>Subscribe to continue reading</h2>
                </div>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 2);

        // Assert
        result.Should().BeTrue("paywall element present and only 2 paragraphs (< 5 threshold)");
    }

    [Fact]
    public void DetectPaywall_ReturnsFalse_WhenPaywallElementsButEnoughParagraphs()
    {
        // Arrange - paywall class present but content has >= 5 paragraphs (full article)
        var html = @"
            <html>
            <body>
                <article>
                    <p>Paragraph one.</p>
                    <p>Paragraph two.</p>
                    <p>Paragraph three.</p>
                    <p>Paragraph four.</p>
                    <p>Paragraph five.</p>
                    <p>Paragraph six.</p>
                </article>
                <div class='paywall-overlay'>
                    <span>Subscribe now</span>
                </div>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 6);

        // Assert
        result.Should().BeFalse("enough paragraphs indicate full content despite paywall element");
    }

    [Fact]
    public void DetectPaywall_ReturnsTrue_WhenPaywallTextAndFewParagraphs()
    {
        // Arrange - no paywall CSS class, but paywall text pattern in body
        var html = @"
            <html>
            <body>
                <article>
                    <p>A brief preview of the article.</p>
                </article>
                <div>
                    <p>Subscribe to The Times to read the full article.</p>
                    <p>Already a subscriber? Log in</p>
                </div>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 1);

        // Assert
        result.Should().BeTrue("paywall text pattern detected and only 1 paragraph (truncated)");
    }

    [Fact]
    public void DetectPaywall_ReturnsFalse_WhenNoPaywallIndicators()
    {
        // Arrange - clean article with no paywall elements or text
        var html = @"
            <html>
            <body>
                <article>
                    <h1>A Normal Article</h1>
                    <p>First paragraph of a freely accessible article.</p>
                    <p>Second paragraph with more details.</p>
                </article>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 2);

        // Assert
        result.Should().BeFalse("no paywall elements or text patterns found");
    }

    [Fact]
    public void DetectPaywall_ReturnsTrue_WhenSubscriberGateClassPresent()
    {
        // Arrange - subscriber-gate class variant
        var html = @"
            <html>
            <body>
                <div class='subscriber-gate visible'>
                    <span>This article is for subscribers</span>
                </div>
                <article>
                    <p>Teaser content only.</p>
                </article>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 1);

        // Assert
        result.Should().BeTrue("subscriber-gate class is a paywall indicator");
    }

    [Fact]
    public void DetectPaywall_ReturnsTrue_WhenPaywallIdPresent()
    {
        // Arrange - id-based paywall detection
        var html = @"
            <html>
            <body>
                <div id='paywall-container'>
                    <span>Please subscribe</span>
                </div>
                <article>
                    <p>Short preview.</p>
                </article>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 1);

        // Assert
        result.Should().BeTrue("id containing 'paywall' is a paywall indicator");
    }

    [Fact]
    public void DetectPaywall_ReturnsFalse_WhenPaywallTextButEnoughParagraphs()
    {
        // Arrange - paywall text present but content is complete
        var html = @"
            <html>
            <body>
                <article>
                    <p>Full paragraph one of the article.</p>
                    <p>Full paragraph two of the article.</p>
                    <p>Full paragraph three of the article.</p>
                    <p>Full paragraph four of the article.</p>
                    <p>Full paragraph five of the article.</p>
                </article>
                <footer>
                    <p>Subscribe to continue reading more articles.</p>
                </footer>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 5);

        // Assert
        result.Should().BeFalse("5 paragraphs meets the threshold even with paywall text");
    }

    [Fact]
    public void DetectPaywall_ReturnsTrue_WhenExpandedDockClassPresent()
    {
        // Arrange - expanded-dock is a known NYT paywall indicator
        var html = @"
            <html>
            <body>
                <div class='expanded-dock'>
                    <span>Become a subscriber</span>
                </div>
                <article>
                    <p>Article teaser.</p>
                    <p>Second short paragraph.</p>
                </article>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphCount: 2);

        // Assert
        result.Should().BeTrue("expanded-dock class is a paywall indicator");
    }

    /// <summary>
    /// Integration test through the public ExtractAsync method to verify paywall detection
    /// flows through to the ReadableContent.IsPaywalled property.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_SetsIsPaywalled_WhenPaywallDetected()
    {
        // Arrange - article with paywall class and truncated content (< 5 substantial paragraphs)
        // Each paragraph must be >50 chars for ExtractParagraphs to keep them, and we need at
        // least 3 paragraphs for IsArticle heuristic, but fewer than 5 for paywall truncation.
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var longText = new string('x', 60);
        var html = $@"
            <html>
            <body>
                <article>
                    <h1>Breaking News Article</h1>
                    <p>{longText} first paragraph content here with more words.</p>
                    <p>{longText} second paragraph content here with more words.</p>
                    <p>{longText} third paragraph content here with more words.</p>
                </article>
                <div class='gateway-content'>
                    <h2>Subscribe to The Times</h2>
                </div>
            </body>
            </html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://www.nytimes.com/article");

        // Assert
        result.Should().NotBeNull();
        result!.IsPaywalled.Should().BeTrue("gateway element + few paragraphs = paywalled");
    }

    [Fact]
    public async Task ExtractAsync_IsPaywalledFalse_WhenNoPaywallIndicators()
    {
        // Arrange - clean article without paywall
        var logger = Substitute.For<ILogger<ReadableContentExtractor>>();
        var extractor = new ReadableContentExtractor(logger);
        var longText = new string('x', 60);
        var html = $@"
            <html>
            <body>
                <article>
                    <h1>Free Article</h1>
                    <p>{longText} first paragraph content here.</p>
                    <p>{longText} second paragraph content here.</p>
                    <p>{longText} third paragraph content here.</p>
                </article>
            </body>
            </html>";

        // Act
        var result = await extractor.ExtractAsync(html, "https://example.com/article");

        // Assert
        result.Should().NotBeNull();
        result!.IsPaywalled.Should().BeFalse("no paywall indicators found");
    }
}
