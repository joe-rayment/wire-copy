using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Service for scraping articles from NYT
/// </summary>
public interface IScraperService
{
    /// <summary>
    /// Scrapes articles from NYT Today's Paper
    /// </summary>
    /// <param name="maxArticles">Maximum number of articles to scrape</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of scraped articles</returns>
    Task<IEnumerable<Article>> ScrapeArticlesAsync(
        int maxArticles = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates to NYT using subscriber credentials
    /// </summary>
    Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default);
}
