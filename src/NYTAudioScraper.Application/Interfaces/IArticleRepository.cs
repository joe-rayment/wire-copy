// <copyright file="IArticleRepository.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Specialized repository for Article operations
/// </summary>
public interface IArticleRepository : IRepository<Article>
{
    /// <summary>
    /// Gets an article by its URL
    /// </summary>
    Task<Article?> GetByUrlAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets articles by section
    /// </summary>
    Task<IEnumerable<Article>> GetBySectionAsync(
        string section,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets articles published within a date range
    /// </summary>
    Task<IEnumerable<Article>> GetByPublishedDateRangeAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an article exists by URL (for cache hit detection)
    /// </summary>
    Task<bool> ExistsByUrlAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recently scraped articles (useful for caching)
    /// </summary>
    Task<IEnumerable<Article>> GetRecentlyScrapedAsync(
        int count = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets articles published on a specific date with optional filters
    /// </summary>
    /// <param name="date">The published date to filter by (UTC)</param>
    /// <param name="section">Optional section filter (comma-separated for multiple)</param>
    /// <param name="maxCount">Maximum number of articles to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Articles published on the specified date, ordered by published date descending</returns>
    Task<IEnumerable<Article>> GetByPublishedDateAsync(
        DateTime date,
        string? section = null,
        int? maxCount = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets articles scraped on a specific date with optional filters.
    /// Use this for Today's Paper workflow where articles may have been published online earlier.
    /// </summary>
    /// <param name="date">The scraped date to filter by (UTC)</param>
    /// <param name="section">Optional section filter (comma-separated for multiple)</param>
    /// <param name="maxCount">Maximum number of articles to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Articles scraped on the specified date, ordered by scraped date descending</returns>
    Task<IEnumerable<Article>> GetByScrapedDateAsync(
        DateTime date,
        string? section = null,
        int? maxCount = null,
        CancellationToken cancellationToken = default);
}
