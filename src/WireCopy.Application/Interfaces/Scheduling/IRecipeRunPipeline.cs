// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>
/// workspace-frpl.8 (B7) — runs one recipe occurrence: resolves each step,
/// assembles a transient collection, generates + publishes the episode, and
/// FINALIZES the supplied <see cref="ScheduledRun"/> (Running → terminal). The
/// caller (the scheduler B6, or TUI run-now B12a) owns the generation gate and
/// the Running row; the pipeline assumes the gate is already held.
/// </summary>
public interface IRecipeRunPipeline
{
    Task RunAsync(ScheduleRecipe recipe, ScheduledRun run, CancellationToken cancellationToken = default);
}
