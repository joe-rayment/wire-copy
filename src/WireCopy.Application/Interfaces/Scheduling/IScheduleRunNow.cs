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
/// workspace-ua0c — the outcome of a blocking run-now PLUS the exact
/// <see cref="ScheduledRun"/> row it created and finalized, so a caller can report
/// THAT run rather than re-querying "the latest finished run for this recipe". The
/// run-recipe verb starts the SchedulerHostedService too; a recipe whose slot is
/// past-grace makes the startup tick write a <c>Skipped</c> row for the same recipe,
/// and a "latest finished run" query (ordered by StartedAtUtc) can pick that Skipped
/// row instead of the run-now result. Reporting the returned <see cref="Run"/>
/// removes that ambiguity entirely. <see cref="Run"/> is null only when
/// <see cref="Outcome"/> is <see cref="RunNowOutcome.Busy"/> (nothing was started).
/// </summary>
public readonly record struct RunNowResult(RunNowOutcome Outcome, ScheduledRun? Run);

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
    /// Runs the recipe to completion under the gate. Returns Busy (with a null
    /// <see cref="RunNowResult.Run"/>, doing nothing) when a generation is already
    /// running; otherwise returns Started together with the finalized ScheduledRun row
    /// it created (workspace-ua0c), so callers report THAT run — never a re-queried
    /// "latest finished run" a concurrent scheduler Skipped row could win. Used by
    /// tests and by the background kickoff; the TUI uses <see cref="StartInBackground"/>
    /// so it stays responsive.
    /// </summary>
    Task<RunNowResult> RunAsync(ScheduleRecipe recipe, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kicks the run off on a background task and returns immediately. Returns Busy
    /// without starting when the gate is already held; otherwise Started.
    /// </summary>
    RunNowOutcome StartInBackground(ScheduleRecipe recipe);
}
