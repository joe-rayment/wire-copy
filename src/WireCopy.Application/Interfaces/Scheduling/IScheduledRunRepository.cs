// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>
/// workspace-frpl.12 (B10) — persistence for <see cref="ScheduledRun"/>, the
/// authoritative run-history + dedup record. Mutations are flushed by the caller
/// via <c>IUnitOfWork.SaveChangesAsync</c> (mirrors IPodcastJobRepository).
/// </summary>
public interface IScheduledRunRepository
{
    Task AddAsync(ScheduledRun run, CancellationToken cancellationToken = default);

    Task UpdateAsync(ScheduledRun run, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledRun>> GetActiveRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>The dedup read: the run row (if any) for a recipe's occurrence.</summary>
    Task<ScheduledRun?> GetByOccurrenceKeyAsync(Guid recipeId, string occurrenceKey, CancellationToken cancellationToken = default);

    /// <summary>Finished runs the user hasn't acknowledged — drives the failure/last-result badge (B11).</summary>
    Task<IReadOnlyList<ScheduledRun>> GetUnacknowledgedFinishedRunsAsync(CancellationToken cancellationToken = default);
}
