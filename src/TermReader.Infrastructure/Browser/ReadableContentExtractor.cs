// Educational and personal use only.

using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Extracts clean, readable article content from HTML.
/// Creates a "reader mode" view similar to browser reader views.
/// </summary>
public partial class ReadableContentExtractor : IReadableContentExtractor
{
    private readonly ILogger<ReadableContentExtractor> _logger;

    private static readonly HashSet<string> ArticleIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "article", "post", "entry", "story", "news", "blog"
    };

    private static readonly HashSet<string> BoilerplateClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "navigation", "menu", "sidebar", "footer", "header", "ad", "advertisement",
        "comment", "comments", "related", "share", "social", "promo", "newsletter",
        "sponsor", "promoted", "popup", "modal", "banner"
    };

    public ReadableContentExtractor(ILogger<ReadableContentExtractor> logger)
    {
        _logger = logger;
    }

    public Task<ReadableContent?> ExtractAsync(string html, string url, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsArticle(html))
            {
                _logger.LogDebug("Page does not appear to be an article: {Url}", url);
                return Task.FromResult<ReadableContent?>(null);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var title = ExtractTitle(doc);
            var author = ExtractAuthor(doc);
            var publishedDate = ExtractPublishedDate(doc);
            var paragraphs = ExtractParagraphs(doc);

            if (paragraphs.Count == 0)
            {
                _logger.LogDebug("No content paragraphs found: {Url}", url);
                return Task.FromResult<ReadableContent?>(null);
            }

            var cleanedText = string.Join("\n\n", paragraphs);

            var content = ReadableContent.Create(
                title ?? "Untitled Article",
                cleanedText,
                paragraphs,
                author,
                publishedDate);

            _logger.LogInformation(
                "Extracted readable content: {Title} ({WordCount} words, {ReadTime} min read)",
                content.Title,
                content.WordCount,
                content.EstimatedReadingMinutes);

            return Task.FromResult<ReadableContent?>(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting readable content from {Url}", url);
            return Task.FromResult<ReadableContent?>(null);
        }
    }

    public bool IsArticle(string html)
    {
        var lowerHtml = html.ToLowerInvariant();

        // Check for article-related meta tags
        if (lowerHtml.Contains("og:type") && lowerHtml.Contains("article"))
        {
            return true;
        }

        // Check for article tag
        if (lowerHtml.Contains("<article"))
        {
            return true;
        }

        // Check for ARIA role attributes
        if (lowerHtml.Contains("role=\"article\"") || lowerHtml.Contains("role=\"main\""))
        {
            return true;
        }

        // Check for common article indicators in classes or IDs
        foreach (var indicator in ArticleIndicators)
        {
            if (lowerHtml.Contains($"class=\"{indicator}") ||
                lowerHtml.Contains($"class='{indicator}") ||
                lowerHtml.Contains($"id=\"{indicator}") ||
                lowerHtml.Contains($"id='{indicator}"))
            {
                return true;
            }
        }

        // Check for common content container classes
        if (lowerHtml.Contains("entry-content") ||
            lowerHtml.Contains("post-content") ||
            lowerHtml.Contains("article-body") ||
            lowerHtml.Contains("article-content"))
        {
            return true;
        }

        // Check for sufficient paragraph content
        var paragraphCount = Regex.Matches(lowerHtml, @"<p[^>]*>").Count;
        if (paragraphCount >= 3)
        {
            return true;
        }

        return false;
    }

    private static string? ExtractTitle(HtmlDocument doc)
    {
        // Try article-specific title selectors
        var titleSelectors = new[]
        {
            "//h1[@class='headline' or contains(@class, 'headline')]",
            "//h1[@itemprop='headline']",
            "//article//h1",
            "//h1[contains(@class, 'title')]",
            "//h1[contains(@class, 'entry-title')]",
            "//h1[contains(@class, 'post-title')]",
            "//h1"
        };

        foreach (var selector in titleSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            var text = CleanText(node?.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        // Fall back to og:title or <title>
        var ogTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", null);
        if (!string.IsNullOrWhiteSpace(ogTitle))
        {
            return ogTitle;
        }

        var titleTag = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        return CleanText(titleTag);
    }

    private static string? ExtractAuthor(HtmlDocument doc)
    {
        // Try meta tags first
        var metaAuthor = doc.DocumentNode.SelectSingleNode("//meta[@name='author']")?.GetAttributeValue("content", null) ??
                        doc.DocumentNode.SelectSingleNode("//meta[@property='article:author']")?.GetAttributeValue("content", null);
        if (!string.IsNullOrWhiteSpace(metaAuthor))
        {
            return CleanAuthorText(metaAuthor);
        }

        // Try element selectors
        var elementSelectors = new[]
        {
            "//*[@itemprop='author']",
            "//*[@class='author' or contains(@class, 'byline')]//a",
            "//*[@class='author' or contains(@class, 'byline')]",
            "//*[@rel='author']"
        };

        foreach (var selector in elementSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            if (node != null)
            {
                var text = CleanText(node.InnerText);
                text = CleanAuthorText(text);

                if (!string.IsNullOrWhiteSpace(text) && text.Length > 1 && text.Length < 100)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string? CleanAuthorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Remove common prefixes
        text = Regex.Replace(text, @"^(by|written by|author:)\s*", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim();
    }

    private static DateTime? ExtractPublishedDate(HtmlDocument doc)
    {
        // Try meta tags with date info
        var metaDate = doc.DocumentNode.SelectSingleNode("//meta[@property='article:published_time']")?.GetAttributeValue("content", null) ??
                      doc.DocumentNode.SelectSingleNode("//meta[@name='pubdate']")?.GetAttributeValue("content", null) ??
                      doc.DocumentNode.SelectSingleNode("//meta[@name='publishdate']")?.GetAttributeValue("content", null);

        if (DateTime.TryParse(metaDate, out var date))
        {
            return date;
        }

        // Try time elements
        var timeNode = doc.DocumentNode.SelectSingleNode("//time[@datetime]") ??
                       doc.DocumentNode.SelectSingleNode("//time[@itemprop='datePublished']");
        var timeAttr = timeNode?.GetAttributeValue("datetime", null);
        if (DateTime.TryParse(timeAttr, out date))
        {
            return date;
        }

        // Try itemprop datePublished
        var datePublished = doc.DocumentNode.SelectSingleNode("//*[@itemprop='datePublished']");
        var contentAttr = datePublished?.GetAttributeValue("content", null) ?? CleanText(datePublished?.InnerText);
        if (DateTime.TryParse(contentAttr, out date))
        {
            return date;
        }

        // Try class-based date elements
        var dateElement = doc.DocumentNode.SelectSingleNode("//*[contains(@class, 'date') or contains(@class, 'publish')]");
        if (dateElement != null && DateTime.TryParse(CleanText(dateElement.InnerText), out date))
        {
            return date;
        }

        return null;
    }

    private List<string> ExtractParagraphs(HtmlDocument doc)
    {
        // Remove boilerplate content first
        RemoveBoilerplate(doc);

        // Try to find the main content area
        var contentArea = FindContentArea(doc);
        if (contentArea == null)
        {
            _logger.LogDebug("No content area found, using full document");
            contentArea = doc.DocumentNode;
        }

        // Extract paragraphs from multiple element types
        var paragraphs = new List<string>();
        var seen = new HashSet<string>();
        var paragraphNodes = contentArea.SelectNodes(".//p | .//blockquote | .//li") ?? Enumerable.Empty<HtmlNode>();

        foreach (var node in paragraphNodes)
        {
            // Skip if inside a boilerplate element
            if (IsInsideBoilerplate(node))
            {
                continue;
            }

            var text = CleanText(node.InnerText);

            // Filter out very short paragraphs (likely navigation or metadata)
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 50 && seen.Add(text))
            {
                // Prefix blockquotes for reader view clarity
                if (node.Name.Equals("blockquote", StringComparison.OrdinalIgnoreCase))
                {
                    paragraphs.Add($"\u201c{text}\u201d");
                }
                else
                {
                    paragraphs.Add(text);
                }
            }
        }

        // If standard elements yielded few results, try divs with direct text content
        if (paragraphs.Count < 3)
        {
            var divNodes = contentArea.SelectNodes(".//div") ?? Enumerable.Empty<HtmlNode>();
            foreach (var div in divNodes)
            {
                if (IsInsideBoilerplate(div))
                {
                    continue;
                }

                // Only consider divs that have direct text (not just child elements)
                var directText = GetDirectTextContent(div);
                if (!string.IsNullOrWhiteSpace(directText) && directText.Length > 50 && seen.Add(directText))
                {
                    paragraphs.Add(directText);
                }
            }
        }

        // If we didn't get enough paragraphs, try a more aggressive approach
        if (paragraphs.Count < 3)
        {
            _logger.LogDebug("Few paragraphs found ({Count}), trying alternative extraction", paragraphs.Count);
            paragraphs = ExtractParagraphsAlternative(contentArea);
        }

        return paragraphs;
    }

    private static void RemoveBoilerplate(HtmlDocument doc)
    {
        var boilerplateSelectors = new[]
        {
            // Semantic elements
            "//nav", "//header", "//footer", "//aside",

            // Navigation and layout
            "//*[contains(@class, 'nav')]",
            "//*[contains(@class, 'sidebar')]",
            "//*[contains(@class, 'comment')]",
            "//*[contains(@class, 'related')]",
            "//*[contains(@class, 'share')]",
            "//*[contains(@class, 'social')]",

            // Ads and promotions
            "//*[contains(@class, 'ad')]",
            "//*[contains(@class, 'advertisement')]",
            "//*[contains(@class, 'promo')]",
            "//*[contains(@class, 'sponsor')]",
            "//*[contains(@class, 'promoted')]",
            "//*[contains(@class, 'newsletter')]",

            // Cookie consent and overlays
            "//*[contains(@class, 'cookie')]",
            "//*[contains(@class, 'consent')]",
            "//*[contains(@id, 'cookie')]",
            "//*[contains(@id, 'consent')]",
            "//*[contains(@class, 'gdpr')]",
            "//*[contains(@id, 'gdpr')]",
            "//*[contains(@class, 'privacy-banner')]",
            "//*[contains(@class, 'cookie-banner')]",

            // Popups and modals
            "//*[contains(@class, 'popup')]",
            "//*[contains(@class, 'modal')]",
            "//*[contains(@class, 'overlay')]",
            "//*[contains(@class, 'paywall')]",

            // Scripts and styles
            "//script", "//style", "//noscript"
        };

        foreach (var selector in boilerplateSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes(selector);
            if (nodes != null)
            {
                foreach (var node in nodes.ToList())
                {
                    node.Remove();
                }
            }
        }
    }

    private static HtmlNode? FindContentArea(HtmlDocument doc)
    {
        // Try common content area selectors (ordered by specificity)
        var contentSelectors = new[]
        {
            "//*[@itemprop='articleBody']",
            "//*[contains(@class, 'article-body')]",
            "//*[contains(@class, 'article-content')]",
            "//*[contains(@class, 'entry-content')]",
            "//*[contains(@class, 'post-content')]",
            "//*[contains(@class, 'story-body')]",
            "//*[contains(@class, 'story-content')]",
            "//article",
            "//*[@role='article']",
            "//*[@role='main']",
            "//main",
            "//*[@id='content']",
            "//*[@class='content']"
        };

        foreach (var selector in contentSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            if (node != null)
            {
                return node;
            }
        }

        return null;
    }

    private static bool IsInsideBoilerplate(HtmlNode node)
    {
        var current = node.ParentNode;
        while (current != null)
        {
            var tagName = current.Name.ToLowerInvariant();
            if (tagName == "nav" || tagName == "footer" || tagName == "aside")
            {
                return true;
            }

            var classAttr = current.GetAttributeValue("class", string.Empty).ToLowerInvariant();
            if (BoilerplateClasses.Any(bc => classAttr.Contains(bc)))
            {
                return true;
            }

            current = current.ParentNode;
        }

        return false;
    }

    private static List<string> ExtractParagraphsAlternative(HtmlNode contentArea)
    {
        var paragraphs = new List<string>();

        // Get all text nodes with substantial content
        var textNodes = contentArea.DescendantsAndSelf()
            .Where(n => n.NodeType == HtmlNodeType.Text)
            .Select(n => CleanText(n.InnerText))
            .Where(t => !string.IsNullOrWhiteSpace(t) && t!.Length > 100)
            .Cast<string>()
            .ToList();

        foreach (var text in textNodes)
        {
            // Split by sentences and group into paragraphs
            var sentences = SentenceRegex().Split(text);
            var currentParagraph = new List<string>();

            foreach (var sentence in sentences)
            {
                var trimmed = sentence.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                currentParagraph.Add(trimmed);

                // Create paragraph every 3-5 sentences
                if (currentParagraph.Count >= 3)
                {
                    paragraphs.Add(string.Join(" ", currentParagraph));
                    currentParagraph.Clear();
                }
            }

            // Add remaining sentences
            if (currentParagraph.Count > 0)
            {
                paragraphs.Add(string.Join(" ", currentParagraph));
            }
        }

        return paragraphs;
    }

    private static string? GetDirectTextContent(HtmlNode node)
    {
        // Collect text from direct text nodes and inline elements only
        var textParts = new List<string>();
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                var text = child.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    textParts.Add(text);
                }
            }
            else if (IsInlineElement(child.Name))
            {
                var text = child.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    textParts.Add(text);
                }
            }
        }

        if (textParts.Count == 0)
        {
            return null;
        }

        return CleanText(string.Join(" ", textParts));
    }

    private static bool IsInlineElement(string tagName)
    {
        return tagName.ToLowerInvariant() switch
        {
            "a" or "span" or "strong" or "b" or "em" or "i" or "u" or "small" or "mark" or "sub" or "sup" or "abbr" or "code" => true,
            _ => false
        };
    }

    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Normalize whitespace
        text = WhitespaceRegex().Replace(text, " ");

        return text.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();
}
