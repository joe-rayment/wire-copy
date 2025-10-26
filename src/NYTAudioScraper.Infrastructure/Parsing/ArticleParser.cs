using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Parsing;

public class ArticleParser
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
            var author = ExtractAuthor(doc);
            var section = ExtractSection(doc);
            var publishedDate = ExtractPublishedDate(doc);

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Failed to extract required fields from article at {Url}", url);
                return null;
            }

            return new Article
            {
                Id = GenerateArticleId(url),
                Title = title,
                Url = url,
                Author = author,
                Section = section,
                Content = content,
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

    private string ExtractTitle(HtmlDocument doc)
    {
        // Try multiple selectors for title
        var titleNode = doc.DocumentNode.SelectSingleNode("//h1[@data-testid='headline']") ??
                       doc.DocumentNode.SelectSingleNode("//h1[contains(@class, 'headline')]") ??
                       doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']") ??
                       doc.DocumentNode.SelectSingleNode("//title");

        if (titleNode != null)
        {
            return titleNode.GetAttributeValue("content", titleNode.InnerText).Trim();
        }

        return string.Empty;
    }

    private string ExtractContent(HtmlDocument doc)
    {
        // Try multiple selectors for article body
        var contentNode = doc.DocumentNode.SelectSingleNode("//section[@name='articleBody']") ??
                         doc.DocumentNode.SelectSingleNode("//article[@id='story']") ??
                         doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'StoryBodyCompanionColumn')]");

        if (contentNode == null)
        {
            return string.Empty;
        }

        // Extract all paragraph text
        var paragraphs = contentNode.SelectNodes(".//p[not(contains(@class, 'css-') and string-length(text()) < 50)]");
        if (paragraphs == null || paragraphs.Count == 0)
        {
            return contentNode.InnerText.Trim();
        }

        return string.Join("\n\n", paragraphs.Select(p => CleanText(p.InnerText)));
    }

    private string? ExtractAuthor(HtmlDocument doc)
    {
        var authorNode = doc.DocumentNode.SelectSingleNode("//meta[@name='author']") ??
                        doc.DocumentNode.SelectSingleNode("//span[@itemprop='author']") ??
                        doc.DocumentNode.SelectSingleNode("//a[@rel='author']");

        return authorNode?.GetAttributeValue("content", authorNode.InnerText).Trim();
    }

    private string? ExtractSection(HtmlDocument doc)
    {
        var sectionNode = doc.DocumentNode.SelectSingleNode("//meta[@property='article:section']") ??
                         doc.DocumentNode.SelectSingleNode("//nav[@data-testid='mini-navigation']//a");

        return sectionNode?.GetAttributeValue("content", sectionNode.InnerText).Trim();
    }

    private DateTime ExtractPublishedDate(HtmlDocument doc)
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

    private string CleanText(string text)
    {
        // Remove excessive whitespace and decode HTML entities
        return HtmlEntity.DeEntitize(text)
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim()
            .Replace("  ", " ");
    }

    private string GenerateArticleId(string url)
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
}
