// <copyright file="IScraperService.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using TermReader.Domain.Entities;

namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for scraping articles from configured source
/// </summary>
public interface IScraperService
{
    /// <summary>
    /// Scrapes articles from Today's Paper
    /// </summary>
    /// <param name="maxArticles">Maximum number of articles to scrape</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of scraped articles</returns>
    Task<IEnumerable<Article>> ScrapeArticlesAsync(
        int maxArticles = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scrapes a single article by its URL
    /// </summary>
    /// <param name="url">Article URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Scraped article or null if failed</returns>
    Task<Article?> ScrapeArticleByUrlAsync(
        string url,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scrapes articles from specific sections of Today's Paper
    /// </summary>
    /// <param name="maxArticles">Maximum number of articles to scrape</param>
    /// <param name="sections">Array of section names to filter (e.g., "Front Page", "Business", "World")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of scraped articles from specified sections</returns>
    Task<IEnumerable<Article>> ScrapeArticlesBySectionsAsync(
        int maxArticles,
        string[] sections,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates using subscriber credentials
    /// </summary>
    Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default);
}
