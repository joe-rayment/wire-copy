// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>Thread-safe in-memory <see cref="IRoutineRunTracker"/>.</summary>
internal sealed class RoutineRunTracker : IRoutineRunTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (RoutineRunProgress Progress, CancellationTokenSource? Cancellation)> _runs = new();

    public event EventHandler? Changed;

    public IReadOnlyList<RoutineRunProgress> Active => Snapshot();

    public RoutineRunProgress? Get(Guid recipeId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(recipeId, out var entry) ? entry.Progress : null;
        }
    }

    public void Begin(RoutineRunProgress progress, CancellationTokenSource? cancellation)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_gate)
        {
            _runs[progress.RecipeId] = (progress, cancellation);
        }

        RaiseChanged();
    }

    public void Update(Guid recipeId, Func<RoutineRunProgress, RoutineRunProgress> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            if (!_runs.TryGetValue(recipeId, out var entry))
            {
                return;
            }

            _runs[recipeId] = (change(entry.Progress), entry.Cancellation);
        }

        RaiseChanged();
    }

    public void SetStage(Guid recipeId, string key, RoutineRunStageState state, string? hint = null, string? detail = null)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(recipeId, out var entry) || !entry.Progress.Stages.Any(s => s.Key == key))
            {
                return;
            }

            var stages = entry.Progress.Stages
                .Select(s => s.Key == key ? s with { State = state, Hint = hint, Detail = detail } : s)
                .ToList();
            _runs[recipeId] = (entry.Progress with { Stages = stages }, entry.Cancellation);
        }

        RaiseChanged();
    }

    public void End(Guid recipeId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _runs.Remove(recipeId);
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    public bool Cancel(Guid recipeId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_runs.TryGetValue(recipeId, out var entry))
            {
                return false;
            }

            cts = entry.Cancellation;
            _runs[recipeId] = (entry.Progress with { CancelRequested = true }, cts);
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished and disposed its token; nothing left to cancel.
        }

        RaiseChanged();
        return true;
    }

    private List<RoutineRunProgress> Snapshot()
    {
        lock (_gate)
        {
            return _runs.Values.Select(r => r.Progress).ToList();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A misbehaving subscriber must never break the run reporting progress.
        }
    }
}
