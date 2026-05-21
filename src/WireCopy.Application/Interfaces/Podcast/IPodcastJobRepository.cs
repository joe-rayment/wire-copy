// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Podcast;
using WireCopy.Domain.Enums.Podcast;

namespace WireCopy.Application.Interfaces.Podcast;

/// <summary>
/// Persistence boundary for <see cref="PodcastJob"/> rows. Modifications
/// are flushed by the caller via <c>IUnitOfWork.SaveChangesAsync</c>, matching
/// the rest of the repository layer (workspace-ur0e Phase F, F.1).
/// </summary>
public interface IPodcastJobRepository
{
    /// <summary>Fetch a single job by id, or null when not found.</summary>
    Task<PodcastJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every row whose status is in
    /// <see cref="PodcastJobStatus.Running"/> or
    /// <see cref="PodcastJobStatus.Pending"/>. Used by
    /// <c>PodcastJobLifecycleService</c> at startup to detect orphans
    /// (workspace-nk06, F.2) and by F.4 to enqueue resumes.
    /// </summary>
    Task<IReadOnlyList<PodcastJob>> GetActiveJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns rows that already finished but have not yet been
    /// acknowledged. Lets the launcher offer "view last podcast result"
    /// without re-querying the orchestrator (F.5).
    /// </summary>
    Task<IReadOnlyList<PodcastJob>> GetUnacknowledgedFinishedJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Add a freshly-created job to the unit of work.</summary>
    Task AddAsync(PodcastJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hint to change-tracking. EF Core will pick up modifications via its
    /// own tracker; this method is here for symmetry with the rest of the
    /// repository layer and so test doubles have a place to record updates.
    /// </summary>
    Task UpdateAsync(PodcastJob job, CancellationToken cancellationToken = default);
}
