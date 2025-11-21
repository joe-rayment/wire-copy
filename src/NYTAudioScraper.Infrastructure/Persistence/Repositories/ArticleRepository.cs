// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository implementation for Article operations.
/// </summary>
public class ArticleRepository : Repository<Article>, IArticleRepository
{
    public ArticleRepository(AppDbContext context)
        : base(context)
    {
    }

    public async Task<Article?> GetByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .FirstOrDefaultAsync(a => a.Url == url, cancellationToken);
    }

    public async Task<IEnumerable<Article>> GetBySectionAsync(
        string section,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(a => a.Section == section)
            .OrderByDescending(a => a.PublishedDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Article>> GetByPublishedDateRangeAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(a => a.PublishedDate >= startDate && a.PublishedDate <= endDate)
            .OrderByDescending(a => a.PublishedDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return await _dbSet.AnyAsync(a => a.Url == url, cancellationToken);
    }

    public async Task<IEnumerable<Article>> GetRecentlyScrapedAsync(
        int count = 100,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .OrderByDescending(a => a.ScrapedDate)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
