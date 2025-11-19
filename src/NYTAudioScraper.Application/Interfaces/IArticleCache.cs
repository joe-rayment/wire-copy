// <copyright file="IArticleCache.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Specialized cache for articles with two-tier caching (memory + database)
/// </summary>
public interface IArticleCache : ICacheService<Article>
{
    /// <summary>
    /// Gets an article by URL (cache key)
    /// </summary>
    Task<Article?> GetByUrlAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Caches an article using its URL as the key
    /// </summary>
    Task CacheArticleAsync(Article article, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets cache statistics
    /// </summary>
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Cache statistics
/// </summary>
public class CacheStatistics
{
    public int MemoryCacheCount { get; set; }
    public int DatabaseCacheCount { get; set; }
    public int HitCount { get; set; }
    public int MissCount { get; set; }
    public double HitRate => HitCount + MissCount > 0 ? (double)HitCount / (HitCount + MissCount) : 0;
}
