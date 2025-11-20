// <copyright file="ArticleParser.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

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

    private static string ExtractTitle(HtmlDocument doc)
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

    private static string ExtractContent(HtmlDocument doc)
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
}
