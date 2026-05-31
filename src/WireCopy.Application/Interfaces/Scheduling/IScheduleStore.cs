// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>
/// workspace-frpl.5 — durable store for recipe DEFINITIONS (one schedules.json).
/// The authoritative run-history / dedup record is the EF ScheduledRun table
/// (B10); this store keeps the editable definitions + a convenience run-state
/// cache. All methods are safe to call from the scheduler loop — they never
/// throw on a missing/corrupt file (they degrade to empty).
/// </summary>
public interface IScheduleStore
{
    Task<IReadOnlyList<ScheduleRecipe>> GetAllAsync();

    Task<ScheduleRecipe?> GetAsync(Guid id);

    Task SaveAsync(ScheduleRecipe recipe);

    Task<bool> DeleteAsync(Guid id);

    /// <summary>
    /// Targeted read-modify-write of ONLY the given recipe's run-state, so a
    /// run-state cache write never clobbers a concurrent definition edit.
    /// </summary>
    Task UpdateRunStateAsync(Guid id, RecipeRunState runState);
}
