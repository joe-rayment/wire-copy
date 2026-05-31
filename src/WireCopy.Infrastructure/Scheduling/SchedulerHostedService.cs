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
/// A missed-past-grace occurrence is recorded Skipped so it isn't re-evaluated.
/// Per-recipe failures only log (never kill the loop).
/// </summary>
internal sealed class SchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PerRunTimeout = TimeSpan.FromMinutes(20);

    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SchedulerHostedService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

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
                    await EvaluateRecipeAsync(scope, recipe, nowLocal, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Scheduler: recipe {Recipe} evaluation failed (non-fatal)", recipe.Name);
                }
            }
        }
        finally
        {
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

    private async Task EvaluateRecipeAsync(
        IServiceScope scope, ScheduleRecipe recipe, DateTimeOffset nowLocal, CancellationToken stoppingToken)
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
        if (existing != null)
        {
            return; // already fired (or recorded skipped) — EF dedup
        }

        if (decision.Kind == SchedulingDecisionKind.MissedPastGrace)
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

        if (decision.Kind != SchedulingDecisionKind.DueNow)
        {
            return;
        }

        var gate = scope.ServiceProvider.GetRequiredService<IPodcastGenerationGate>();
        if (!gate.TryAcquire(out var lease))
        {
            _logger.LogInformation("Scheduler: {Recipe} due but a generation is in progress — deferring", recipe.Name);
            return; // defer to a later tick; no row written so it stays due
        }

        // Write the Running row FIRST (crash-safe dedup + orphan-sweep target).
        var run = ScheduledRun.Start(recipe.Id, recipe.Name, occurrenceKey);
        await runRepo.AddAsync(run, stoppingToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken).ConfigureAwait(false);

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
