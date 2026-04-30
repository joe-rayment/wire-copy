// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for paywall detection in ReadableContentExtractor.
/// DetectPaywall is internal static and WireCopy.Tests has InternalsVisibleTo access.
/// </summary>
[Trait("Category", "Unit")]
public class PaywallDetectionTests
{
    [Fact]
    public void DetectPaywall_ReturnsTrue_WhenPaywallElementsAndFewParagraphs()
    {
        // Arrange - NYT-style gateway class with fewer than 3 short paragraphs
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

        var paragraphs = new List<string>
        {
            "First paragraph of the article with some content.",
            "Second paragraph continues the story."
        };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

        // Assert
        result.Should().BeTrue("paywall element present, only 2 paragraphs, and short total content");
    }

    [Fact]
    public void DetectPaywall_ReturnsTrue_WhenPaywallElementsEvenWithEnoughParagraphs()
    {
        // Arrange - paywall class present with 6 paragraphs of preview content.
        // NYT and similar sites show preview content before the paywall gate,
        // so paywall HTML elements are always trusted.
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

        var paragraphs = new List<string>
        {
            "Paragraph one.", "Paragraph two.", "Paragraph three.",
            "Paragraph four.", "Paragraph five.", "Paragraph six."
        };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

        // Assert
        result.Should().BeTrue("paywall HTML elements are explicit gate indicators — always trusted");
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

        var paragraphs = new List<string> { "A brief preview of the article." };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

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

        var paragraphs = new List<string>
        {
            "First paragraph of a freely accessible article.",
            "Second paragraph with more details."
        };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

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

        var paragraphs = new List<string> { "Teaser content only." };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

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

        var paragraphs = new List<string> { "Short preview." };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

        // Assert
        result.Should().BeTrue("id containing 'paywall' is a paywall indicator");
    }

    [Fact]
    public void DetectPaywall_ReturnsFalse_WhenPaywallTextButEnoughParagraphs()
    {
        // Arrange - paywall text present but content has >= 3 paragraphs (full article)
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

        var paragraphs = new List<string>
        {
            "Full paragraph one of the article.",
            "Full paragraph two of the article.",
            "Full paragraph three of the article.",
            "Full paragraph four of the article.",
            "Full paragraph five of the article."
        };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

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

        var paragraphs = new List<string>
        {
            "Article teaser.",
            "Second short paragraph."
        };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

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
        // Arrange - article with paywall class and truncated content (< 3 substantial paragraphs
        // and total content < 500 chars). We need >= 3 <p> tags so IsArticle returns true,
        // but only 2 should be substantial (> 50 chars) to simulate a teaser.
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
                    <p>Short.</p>
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
        result!.IsPaywalled.Should().BeTrue("gateway element + few short paragraphs = paywalled");
    }

    [Fact]
    public void DetectPaywall_ReturnsFalse_WhenPaywallTextButSubstantialContent()
    {
        // Arrange - short article (2 paragraphs) with subscription CTA text,
        // but paragraphs are substantive (total >= 500 chars). This is the false-positive
        // scenario: a legitimately short article with a subscription footer.
        var html = @"
            <html>
            <body>
                <article>
                    <p>A substantive paragraph about an important topic with enough detail to be meaningful.</p>
                    <p>Another paragraph with more details and context about the same topic.</p>
                </article>
                <footer>
                    <p>Subscribe to continue reading more articles from our publication.</p>
                </footer>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Two paragraphs with total content >= 500 chars
        var paragraphs = new List<string>
        {
            new string('A', 260) + " substantive paragraph about an important topic.",
            new string('B', 260) + " another paragraph with more details and context."
        };

        // Act
        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

        // Assert
        result.Should().BeFalse(
            "even though paywall text is present and paragraph count is low, " +
            "the total content length (>= 500 chars) indicates a real short article, not a truncated teaser");
    }

    [Fact]
    public void DetectPaywall_ReturnsTrue_NytStylePreviewWithGateway()
    {
        // NYT shows 5+ paragraphs of preview before the paywall gate.
        // The gateway element must trigger detection regardless of content amount.
        var html = @"
            <html>
            <body>
                <article>
                    <p>The first paragraph of a long article about important news.</p>
                    <p>The second paragraph continues the story with more detail.</p>
                    <p>A third paragraph adds context and background information.</p>
                    <p>The fourth paragraph quotes an expert on the topic.</p>
                    <p>A fifth paragraph wraps up the preview section of the article.</p>
                </article>
                <div class='gateway-content' id='gateway-content'>
                    <h2>Subscribe to The Times</h2>
                    <p>Already a subscriber? Log in</p>
                </div>
            </body>
            </html>";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var paragraphs = new List<string>
        {
            "The first paragraph of a long article about important news.",
            "The second paragraph continues the story with more detail.",
            "A third paragraph adds context and background information.",
            "The fourth paragraph quotes an expert on the topic.",
            "A fifth paragraph wraps up the preview section of the article."
        };

        var result = ReadableContentExtractor.DetectPaywall(doc, paragraphs);

        result.Should().BeTrue("gateway element is a strong paywall indicator even with 5 preview paragraphs");
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
