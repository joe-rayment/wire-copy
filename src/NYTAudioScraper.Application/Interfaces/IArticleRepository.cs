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
}
