// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>Tracks which routine runs are in flight and how far they are, and lets a screen ask one to stop.</summary>
public interface IRoutineRunTracker
{
    /// <summary>Raised (on any thread) when a run begins, changes or ends.</summary>
    event EventHandler? Changed;

    /// <summary>Gets a snapshot of the in-flight runs.</summary>
    IReadOnlyList<RoutineRunProgress> Active { get; }

    /// <summary>The routine's in-flight run, or null.</summary>
    /// <param name="recipeId">The routine's id.</param>
    /// <returns>The progress, or null when not running.</returns>
    RoutineRunProgress? Get(Guid recipeId);

    /// <summary>Starts tracking a run, replacing any entry for the same routine.</summary>
    /// <param name="progress">Initial progress.</param>
    /// <param name="cancellation">Token source cancelled by <see cref="Cancel"/>.</param>
    void Begin(RoutineRunProgress progress, CancellationTokenSource? cancellation);

    /// <summary>Applies a change; no-op when the routine is not running.</summary>
    /// <param name="recipeId">The routine's id.</param>
    /// <param name="change">Maps old progress to new.</param>
    void Update(Guid recipeId, Func<RoutineRunProgress, RoutineRunProgress> change);

    /// <summary>Replaces that stage's state/hint/detail; no-op when the routine or stage is unknown.</summary>
    /// <param name="recipeId">The routine's id.</param>
    /// <param name="key">Stage key.</param>
    /// <param name="state">New state.</param>
    /// <param name="hint">New hint.</param>
    /// <param name="detail">New detail.</param>
    void SetStage(Guid recipeId, string key, RoutineRunStageState state, string? hint = null, string? detail = null);

    /// <summary>Stops tracking the routine's run.</summary>
    /// <param name="recipeId">The routine's id.</param>
    void End(Guid recipeId);

    /// <summary>Marks CancelRequested and cancels the run's token.</summary>
    /// <param name="recipeId">The routine's id.</param>
    /// <returns>False when not running.</returns>
    bool Cancel(Guid recipeId);
}
