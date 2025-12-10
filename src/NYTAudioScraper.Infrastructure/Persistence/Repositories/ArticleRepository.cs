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

    public async Task<IEnumerable<Article>> GetByPublishedDateAsync(
        DateTime date,
        string? section = null,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1).AddTicks(-1);

        var query = _dbSet
            .Where(a => a.PublishedDate >= startOfDay && a.PublishedDate <= endOfDay);

        if (!string.IsNullOrWhiteSpace(section))
        {
            var sections = section.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(a => a.Section != null && sections.Contains(a.Section));
        }

        query = query.OrderByDescending(a => a.PublishedDate);

        if (maxCount.HasValue && maxCount.Value > 0)
        {
            query = query.Take(maxCount.Value);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Article>> GetByScrapedDateAsync(
        DateTime date,
        string? section = null,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1).AddTicks(-1);

        var query = _dbSet
            .Where(a => a.ScrapedDate >= startOfDay && a.ScrapedDate <= endOfDay);

        if (!string.IsNullOrWhiteSpace(section))
        {
            var sections = section.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(a => a.Section != null && sections.Contains(a.Section));
        }

        query = query.OrderByDescending(a => a.ScrapedDate);

        if (maxCount.HasValue && maxCount.Value > 0)
        {
            query = query.Take(maxCount.Value);
        }

        return await query.ToListAsync(cancellationToken);
    }
}
