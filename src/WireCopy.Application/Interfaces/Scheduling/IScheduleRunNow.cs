// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>workspace-frpl.14 (B12a) — outcome of a user-triggered "run now".</summary>
public enum RunNowOutcome
{
    /// <summary>The run was started (and, for the blocking overload, completed).</summary>
    Started,

    /// <summary>A generation is already in progress; the gate was held, so nothing started.</summary>
    Busy,
}

/// <summary>
/// workspace-frpl.14 (B12a) — runs a recipe immediately from the Schedules screen,
/// reusing the SAME admission protocol as the scheduler (B6): acquire the B0
/// generation gate (or report Busy), write a Running <see cref="ScheduledRun"/> row
/// FIRST, hand to the run pipeline (B7), and release the gate in a finally — under a
/// UNIQUE "run-now@…" occurrence key so it never collides with a scheduled
/// occurrence's dedup row. The finalized run is surfaced by B11 like any other.
/// </summary>
public interface IScheduleRunNow
{
    /// <summary>
    /// Runs the recipe to completion under the gate. Returns Busy (and does nothing)
    /// when a generation is already running. Used by tests and by the background
    /// kickoff; the TUI uses <see cref="StartInBackground"/> so it stays responsive.
    /// </summary>
    Task<RunNowOutcome> RunAsync(ScheduleRecipe recipe, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kicks the run off on a background task and returns immediately. Returns Busy
    /// without starting when the gate is already held; otherwise Started.
    /// </summary>
    RunNowOutcome StartInBackground(ScheduleRecipe recipe);
}
