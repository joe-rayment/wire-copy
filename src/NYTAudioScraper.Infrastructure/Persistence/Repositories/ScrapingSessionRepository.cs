using Microsoft.EntityFrameworkCore;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository implementation for ScrapingSession operations
/// </summary>
public class ScrapingSessionRepository : Repository<ScrapingSession>, IScrapingSessionRepository
{
    public ScrapingSessionRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<ScrapingSession>> GetSessionsByDateRangeAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.StartedAt >= startDate && s.StartedAt <= endDate)
            .Include(s => s.Articles)
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<ScrapingSession>> GetSessionsByStatusAsync(
        ScrapingStatus status,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.Status == status)
            .Include(s => s.Articles)
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ScrapingSession?> GetLastIncompleteSessionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.Status == ScrapingStatus.InProgress || s.Status == ScrapingStatus.PartiallyCompleted)
            .Include(s => s.Articles)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<decimal> GetTotalCostAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.Status == ScrapingStatus.Completed || s.Status == ScrapingStatus.PartiallyCompleted)
            .SumAsync(s => s.EstimatedCost, cancellationToken);
    }

    public async Task<decimal> GetTotalCostByDateRangeAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.StartedAt >= startDate && s.StartedAt <= endDate)
            .Where(s => s.Status == ScrapingStatus.Completed || s.Status == ScrapingStatus.PartiallyCompleted)
            .SumAsync(s => s.EstimatedCost, cancellationToken);
    }

    public override async Task<ScrapingSession?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(s => s.Articles)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }
}
