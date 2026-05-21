// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Podcast;
using WireCopy.Domain.Enums.Podcast;

namespace WireCopy.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IPodcastJobRepository"/>.
/// </summary>
public class PodcastJobRepository : IPodcastJobRepository
{
    private readonly AppDbContext _context;

    public PodcastJobRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<PodcastJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _context.Set<PodcastJob>()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<PodcastJob>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<PodcastJob>()
            .Where(j => j.Status == PodcastJobStatus.Running || j.Status == PodcastJobStatus.Pending)
            .OrderByDescending(j => j.StartedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PodcastJob>> GetUnacknowledgedFinishedJobsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<PodcastJob>()
            .Where(j => j.AcknowledgedAtUtc == null
                && (j.Status == PodcastJobStatus.FullSuccess
                    || j.Status == PodcastJobStatus.PartialSuccess
                    || j.Status == PodcastJobStatus.TotalFailure
                    || j.Status == PodcastJobStatus.Cancelled
                    || j.Status == PodcastJobStatus.Interrupted))
            .OrderByDescending(j => j.LastProgressAtUtc)
            .ToListAsync(cancellationToken);
    }

    public Task AddAsync(PodcastJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        _context.Set<PodcastJob>().Add(job);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(PodcastJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // EF Core change tracking already detects mutations; the call exists
        // so the interface is symmetric and test doubles can record updates.
        return Task.CompletedTask;
    }
}
