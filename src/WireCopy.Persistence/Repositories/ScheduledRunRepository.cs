// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;

namespace WireCopy.Persistence.Repositories;

/// <summary>workspace-frpl.12 (B10) — EF Core implementation of <see cref="IScheduledRunRepository"/>.</summary>
public class ScheduledRunRepository : IScheduledRunRepository
{
    private readonly AppDbContext _context;

    public ScheduledRunRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task AddAsync(ScheduledRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _context.Set<ScheduledRun>().Add(run);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ScheduledRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // EF change tracking already detects mutations; symmetric for test doubles.
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ScheduledRun>> GetActiveRunsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<ScheduledRun>()
            .Where(r => r.Status == ScheduledRunStatus.Running || r.Status == ScheduledRunStatus.Pending)
            .OrderByDescending(r => r.StartedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public Task<ScheduledRun?> GetByOccurrenceKeyAsync(Guid recipeId, string occurrenceKey, CancellationToken cancellationToken = default)
    {
        return _context.Set<ScheduledRun>()
            .FirstOrDefaultAsync(r => r.RecipeId == recipeId && r.OccurrenceKey == occurrenceKey, cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduledRun>> GetUnacknowledgedFinishedRunsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<ScheduledRun>()
            .Where(r => r.AcknowledgedAtUtc == null
                && (r.Status == ScheduledRunStatus.Completed
                    || r.Status == ScheduledRunStatus.PartialSuccess
                    || r.Status == ScheduledRunStatus.Failed
                    || r.Status == ScheduledRunStatus.Skipped
                    || r.Status == ScheduledRunStatus.Interrupted))
            .OrderByDescending(r => r.StartedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
