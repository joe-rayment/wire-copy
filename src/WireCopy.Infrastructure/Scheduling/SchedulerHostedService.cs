// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.7 (B6) — the in-process scheduler. While the app is open it
/// ticks (off an injectable <see cref="TimeProvider"/>) and, for each enabled
/// recipe whose occurrence is due AND has no ScheduledRun row yet (the EF row is
/// the AUTHORITATIVE dedup, not the JSON cache), it: acquires the B0 generation
/// gate (defers if held), writes a Running ScheduledRun row FIRST (crash-safe
/// dedup), then hands to the run pipeline (B7), releasing the gate in a finally.
/// A missed-past-grace occurrence is recorded Skipped so it isn't re-evaluated —
/// UNLESS the scheduler itself was alive at slot time but too busy to look (a
/// manual generation held the gate, or an earlier recipe's long run in the same
/// sequential pass spanned the slot): that occurrence is still owed and fires
/// late (workspace-bgu9l). Per-recipe failures only log (never kill the loop).
/// </summary>
internal sealed class SchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PerRunTimeout = TimeSpan.FromMinutes(20);

    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SchedulerHostedService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    // workspace-bgu9l — in-memory evidence that the scheduler was alive when a
    // slot came due, so NextDueCalculator's pure, clock-only MissedPastGrace is
    // not mistaken for "the app was closed". Both fields are guarded by _runLock.
    //
    // Occurrences owed a run: deferred on a held gate, or rescued from a busy
    // pass. Removed once a ScheduledRun row exists for them.
    private readonly HashSet<(Guid RecipeId, string OccurrenceKey)> _owed = new();

    // The previous pass's span: Start = the nowLocal it decided against, End =
    // when it finished. A slot in (Start, End] was never looked at because that
    // pass was awaiting a recipe run. Only the immediately preceding pass
    // matters — the next pass decides every slot up to its own start.
    private (DateTimeOffset Start, DateTimeOffset End)? _previousPass;

    public SchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<SchedulerHostedService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// One scheduler tick. Exposed for tests so cadence behaviour is provable by
    /// passing an explicit <paramref name="nowLocal"/> — no real waits.
    /// </summary>
    internal async Task EvaluateOnceAsync(DateTimeOffset nowLocal, CancellationToken stoppingToken)
    {
        await _runLock.WaitAsync(stoppingToken).ConfigureAwait(false);
        var previousPass = _previousPass;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
            var recipes = await store.GetAllAsync().ConfigureAwait(false);

            foreach (var recipe in recipes.Where(r => r.Enabled))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await EvaluateRecipeAsync(scope, recipe, nowLocal, previousPass, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Scheduler: recipe {Recipe} evaluation failed (non-fatal)", recipe.Name);
                }
            }
        }
        finally
        {
            // Read from the injected clock: after a long recipe run this is later
            // than nowLocal, and that gap is exactly what the next pass needs.
            _previousPass = (nowLocal, _timeProvider.GetLocalNow());
            _runLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await EvaluateOnceAsync(_timeProvider.GetLocalNow(), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    /// <summary>
    /// True when <see cref="NextDueCalculator"/> reports MissedPastGrace only because
    /// the scheduler was occupied at slot time: the occurrence was already deferred
    /// on a held gate (or rescued once), or its slot fell inside the previous pass's
    /// busy span. A slot that passed while the app was closed is in neither.
    /// </summary>
    private bool CameDueWhileSchedulerWasAlive(
        ScheduleRecipe recipe,
        (Guid RecipeId, string OccurrenceKey) owedKey,
        DateTimeOffset nowLocal,
        (DateTimeOffset Start, DateTimeOffset End)? previousPass)
    {
        if (_owed.Contains(owedKey))
        {
            return true;
        }

        if (previousPass is not { } pass)
        {
            return false;
        }

        // Same wall-clock slot NextDueCalculator decided on (today, at the cadence time).
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var time = recipe.Cadence.LocalTime;
        var slot = new DateTimeOffset(today.Year, today.Month, today.Day, time.Hour, time.Minute, 0, nowLocal.Offset);
        return slot > pass.Start && slot <= pass.End;
    }

    private async Task EvaluateRecipeAsync(
        IServiceScope scope,
        ScheduleRecipe recipe,
        DateTimeOffset nowLocal,
        (DateTimeOffset Start, DateTimeOffset End)? previousPass,
        CancellationToken stoppingToken)
    {
        var runRepo = scope.ServiceProvider.GetRequiredService<IScheduledRunRepository>();

        // Decide against a NEUTRAL run-state — the authoritative "already ran"
        // signal is the EF row, not the JSON cache (so a JSON/EF divergence
        // never silently skips a due occurrence).
        var decision = NextDueCalculator.Decide(recipe.Cadence, Domain.ValueObjects.Scheduling.RecipeRunState.Initial, nowLocal);
        if (decision.OccurrenceKey is not { } occurrenceKey)
        {
            return; // NotDue
        }

        var existing = await runRepo.GetByOccurrenceKeyAsync(recipe.Id, occurrenceKey, stoppingToken).ConfigureAwait(false);
        var owedKey = (recipe.Id, occurrenceKey);
        if (existing != null)
        {
            _owed.Remove(owedKey);
            return; // already fired (or recorded skipped) — EF dedup
        }

        var kind = decision.Kind;
        if (kind == SchedulingDecisionKind.MissedPastGrace
            && CameDueWhileSchedulerWasAlive(recipe, owedKey, nowLocal, previousPass))
        {
            // "Past grace" only because the scheduler was busy, not because the
            // app was closed — the occurrence is still owed (workspace-bgu9l).
            _logger.LogInformation("Scheduler: {Recipe} slot {Slot} came due while the scheduler was busy — firing late", recipe.Name, occurrenceKey);
            _owed.Add(owedKey); // stays owed until a row exists, even if this tick fails before writing one
            kind = SchedulingDecisionKind.DueNow;
        }

        if (kind == SchedulingDecisionKind.MissedPastGrace)
        {
            var skipped = ScheduledRun.Start(recipe.Id, recipe.Name, occurrenceKey);
            skipped.Finish(
                ScheduledRunStatus.Skipped,
                itemCount: 0,
                errorClass: "MissedPastGrace",
                errorMessage: "App was not open within the recipe's grace window");
            await runRepo.AddAsync(skipped, stoppingToken).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        if (kind != SchedulingDecisionKind.DueNow)
        {
            return;
        }

        var gate = scope.ServiceProvider.GetRequiredService<IPodcastGenerationGate>();
        if (!gate.TryAcquire(out var lease))
        {
            _logger.LogInformation("Scheduler: {Recipe} due but a generation is in progress — deferring", recipe.Name);
            _owed.Add(owedKey); // no row written, and it must stay due even once past grace
            return; // defer to a later tick
        }

        // Write the Running row FIRST (crash-safe dedup + orphan-sweep target).
        var run = ScheduledRun.Start(recipe.Id, recipe.Name, occurrenceKey);
        await runRepo.AddAsync(run, stoppingToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken).ConfigureAwait(false);
        _owed.Remove(owedKey); // the row is now the dedup

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        runCts.CancelAfter(PerRunTimeout);
        try
        {
            var pipeline = scope.ServiceProvider.GetService<IRecipeRunPipeline>();
            if (pipeline == null)
            {
                run.Finish(
                    ScheduledRunStatus.Failed,
                    itemCount: 0,
                    errorClass: "NoPipeline",
                    errorMessage: "Recipe run pipeline is not registered");
                await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                return;
            }

            await pipeline.RunAsync(recipe, run, runCts.Token).ConfigureAwait(false);
        }
        finally
        {
            lease!.Dispose();
        }
    }
}
