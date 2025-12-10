// Educational and personal use only.

using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Parsing;

public class ArticleParser : IArticleParser
{
    private readonly ILogger<ArticleParser> _logger;

    public ArticleParser(ILogger<ArticleParser> logger)
    {
        _logger = logger;
    }

    public Article? ParseArticle(string html, string url)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var title = ExtractTitle(doc);
            var content = ExtractContent(doc);

            // Log diagnostic info about what was found
            var hasTitle = !string.IsNullOrWhiteSpace(title);
            var hasContent = !string.IsNullOrWhiteSpace(content);
            var contentLength = content?.Length ?? 0;

            _logger.LogDebug(
                "Parse result for {Url}: Title={HasTitle}, Content={HasContent} ({ContentLength} chars), PageSize={PageSize}",
                url,
                hasTitle ? "found" : "MISSING",
                hasContent ? "found" : "MISSING",
                contentLength,
                html.Length);

            // Only title and content are required
            if (!hasTitle || !hasContent)
            {
                // Show actual page content for diagnosis instead of guessing
                var isChallenge = DetectChallengePage(doc);
                var pagePreview = GetPagePreview(doc, 500);

                if (isChallenge)
                {
                    _logger.LogWarning(
                        "CHALLENGE PAGE detected for {Url}. Bot detection triggered. Preview: {Preview}",
                        url,
                        pagePreview);
                }
                else
                {
                    _logger.LogWarning(
                        "Extraction failed for {Url}: Title={HasTitle}, Content={HasContent}. Page preview: {Preview}",
                        url,
                        hasTitle ? "OK" : "MISSING",
                        hasContent ? "OK" : "MISSING",
                        pagePreview);
                }

                return null;
            }

            // These are optional - log if missing but don't fail
            var author = ExtractAuthor(doc);
            var section = ExtractSection(doc);
            var publishedDate = ExtractPublishedDate(doc);

            if (string.IsNullOrWhiteSpace(author))
            {
                _logger.LogDebug("No author found for {Url} (optional field)", url);
            }

            if (string.IsNullOrWhiteSpace(section))
            {
                _logger.LogDebug("No section found for {Url} (optional field)", url);
            }

            return new Article
            {
                Id = GenerateArticleId(url),
                Title = title!,  // Already validated hasTitle above
                Url = url,
                Author = author,
                Section = section,
                Content = content!,  // Already validated hasContent above
                PublishedDate = publishedDate,
                ScrapedDate = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing article from {Url}", url);
            return null;
        }
    }

    private static string? ExtractAuthor(HtmlDocument doc)
    {
        // Try multiple selectors for author/byline
        // First try meta tag
        var authorNode = doc.DocumentNode.SelectSingleNode("//meta[@name='author']");
        if (authorNode != null)
        {
            var authorContent = authorNode.GetAttributeValue("content", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(authorContent))
            {
                return authorContent;
            }
        }

        // Try to find byline links (e.g., <a class="last-byline" itemprop="name">)
        var bylineNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'byline') and @itemprop='name']") ??
                         doc.DocumentNode.SelectNodes("//a[@itemprop='name' and contains(@href, '/by/')]") ??
                         doc.DocumentNode.SelectNodes("//span[@itemprop='author']//a") ??
                         doc.DocumentNode.SelectNodes("//a[@rel='author']");

        if (bylineNodes != null && bylineNodes.Count > 0)
        {
            var authors = bylineNodes
                .Select(node => CleanText(node.InnerText))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct()
                .ToList();

            if (authors.Count > 0)
            {
                return string.Join(", ", authors);
            }
        }

        return null;
    }

    private static string? ExtractSection(HtmlDocument doc)
    {
        var sectionNode = doc.DocumentNode.SelectSingleNode("//meta[@property='article:section']") ??
                         doc.DocumentNode.SelectSingleNode("//nav[@data-testid='mini-navigation']//a");

        return sectionNode?.GetAttributeValue("content", sectionNode.InnerText).Trim();
    }

    private static DateTime ExtractPublishedDate(HtmlDocument doc)
    {
        var dateNode = doc.DocumentNode.SelectSingleNode("//meta[@property='article:published_time']") ??
                      doc.DocumentNode.SelectSingleNode("//time[@datetime]");

        if (dateNode != null)
        {
            var dateString = dateNode.GetAttributeValue("content", dateNode.GetAttributeValue("datetime", string.Empty));
            if (DateTime.TryParse(dateString, out var date))
            {
                return date;
            }
        }

        return DateTime.UtcNow;
    }

    private static string CleanText(string text)
    {
        // Remove excessive whitespace and decode HTML entities
        return HtmlEntity.DeEntitize(text)
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim()
            .Replace("  ", " ");
    }

    private static string GenerateArticleId(string url)
    {
        // Extract article ID from NYT URL pattern: /YYYY/MM/DD/section/article-slug.html
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 4)
        {
            return string.Join("-", segments.Skip(segments.Length - 1));
        }

        // Fallback to hash of URL
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url)))[..16];
    }

    private static bool DetectChallengePage(HtmlDocument doc)
    {
        var bodyText = doc.DocumentNode.InnerText?.ToLowerInvariant() ?? string.Empty;
        var challengeIndicators = new[]
        {
            "verify you are human",
            "checking your browser",
            "datadome",
            "captcha",
            "please wait",
            "access denied",
            "enable javascript",
            "one more step",
            "security check"
        };
        return Array.Exists(challengeIndicators, indicator => bodyText.Contains(indicator));
    }

    private static string GetPagePreview(HtmlDocument doc, int maxLength)
    {
        var text = doc.DocumentNode.InnerText?.Trim() ?? string.Empty;

        // Collapse whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }

    private string ExtractTitle(HtmlDocument doc)
    {
        // Try multiple selectors for title in order of specificity
        var selectors = new[]
        {
            "//h1[@data-testid='headline']",
            "//h1[contains(@class, 'headline')]",
            "//h1[contains(@class, 'css-') and string-length(text()) > 10]",
            "//article//h1",
            "//meta[@property='og:title']",
            "//title",
        };

        foreach (var selector in selectors)
        {
            var titleNode = doc.DocumentNode.SelectSingleNode(selector);
            if (titleNode != null)
            {
                var title = titleNode.GetAttributeValue("content", titleNode.InnerText).Trim();
                if (!string.IsNullOrWhiteSpace(title) && title.Length > 5)
                {
                    _logger.LogDebug("Title found using selector: {Selector}", selector);
                    return title;
                }
            }
        }

        _logger.LogDebug("No title found. Tried selectors: {Selectors}", string.Join(", ", selectors));
        return string.Empty;
    }

    private string ExtractContent(HtmlDocument doc)
    {
        // Try multiple selectors for article body in order of specificity
        var selectors = new[]
        {
            "//section[@name='articleBody']",
            "//article[@id='story']",
            "//div[contains(@class, 'StoryBodyCompanionColumn')]",
            "//div[@data-testid='article-body']",
            "//div[contains(@class, 'article-body')]",
            "//article//div[contains(@class, 'css-') and .//p]",
            "//main//article",
        };

        HtmlNode? contentNode = null;
        string? matchedSelector = null;

        foreach (var selector in selectors)
        {
            contentNode = doc.DocumentNode.SelectSingleNode(selector);
            if (contentNode != null)
            {
                matchedSelector = selector;
                break;
            }
        }

        if (contentNode == null)
        {
            _logger.LogDebug("No content container found. Tried selectors: {Selectors}", string.Join(", ", selectors));
            return string.Empty;
        }

        _logger.LogDebug("Content found using selector: {Selector}", matchedSelector);

        // Extract all paragraph text - be more permissive with the filter
        var paragraphs = contentNode.SelectNodes(".//p");
        if (paragraphs == null || paragraphs.Count == 0)
        {
            var fallbackText = contentNode.InnerText.Trim();
            _logger.LogDebug("No paragraphs found, using container text ({Length} chars)", fallbackText.Length);
            return fallbackText;
        }

        // Filter out very short paragraphs that are likely UI elements
        var validParagraphs = paragraphs
            .Select(p => CleanText(p.InnerText))
            .Where(text => text.Length > 30) // Filter out short UI strings
            .ToList();

        if (validParagraphs.Count == 0)
        {
            // Fall back to all paragraphs if filtering was too aggressive
            validParagraphs = paragraphs
                .Select(p => CleanText(p.InnerText))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
        }

        _logger.LogDebug("Extracted {Count} paragraphs from content", validParagraphs.Count);
        return string.Join("\n\n", validParagraphs);
    }
}
