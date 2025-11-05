using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Specialized repository for ScrapingSession operations
/// </summary>
public interface IScrapingSessionRepository : IRepository<ScrapingSession>
{
    /// <summary>
    /// Gets all sessions within a date range
    /// </summary>
    Task<IEnumerable<ScrapingSession>> GetSessionsByDateRangeAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets sessions by status
    /// </summary>
    Task<IEnumerable<ScrapingSession>> GetSessionsByStatusAsync(
        ScrapingStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent incomplete session (for resume functionality)
    /// </summary>
    Task<ScrapingSession?> GetLastIncompleteSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets total cost across all sessions
    /// </summary>
    Task<decimal> GetTotalCostAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets total cost within a date range
    /// </summary>
    Task<decimal> GetTotalCostByDateRangeAsync(
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);
}
