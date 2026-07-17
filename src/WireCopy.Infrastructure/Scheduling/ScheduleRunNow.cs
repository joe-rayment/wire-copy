// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.14 (B12a) — the "run now" coordinator. Mirrors
/// <c>SchedulerHostedService.EvaluateRecipeAsync</c>'s admission protocol so a manual
/// run and a scheduled run can never contend: TryAcquire the B0 gate (Busy if held),
/// write a Running ScheduledRun row FIRST under a unique <c>run-now@…</c> occurrence
/// key, run the pipeline (which finalizes the row), and release the lease in a
/// finally. The unique key guarantees a manual run never overwrites — or is deduped
/// against — a scheduled occurrence's row.
/// </summary>
internal sealed class ScheduleRunNow : IScheduleRunNow
{
    private static long _sequence;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPodcastGenerationGate _gate;
    private readonly ILogger<ScheduleRunNow> _logger;
    private readonly TimeProvider _timeProvider;

    public ScheduleRunNow(
        IServiceScopeFactory scopeFactory,
        IPodcastGenerationGate gate,
        ILogger<ScheduleRunNow> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _gate = gate;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RunNowOutcome StartInBackground(ScheduleRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (_gate.IsHeld)
        {
            return RunNowOutcome.Busy;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(recipe, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run-now for recipe {Recipe} failed", recipe.Name);
            }
        });
        return RunNowOutcome.Started;
    }

    public async Task<RunNowResult> RunAsync(ScheduleRecipe recipe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (!_gate.TryAcquire(out var lease))
        {
            _logger.LogInformation("Run-now for {Recipe} skipped — a generation is already in progress", recipe.Name);
            return new RunNowResult(RunNowOutcome.Busy, null);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();

            // A monotonic suffix guarantees uniqueness even for two run-nows within the
            // same clock tick, so a manual run never collides with another run's (or a
            // scheduled occurrence's) dedup row.
            var seq = System.Threading.Interlocked.Increment(ref _sequence);
            var occurrenceKey = $"run-now@{_timeProvider.GetUtcNow().UtcDateTime:yyyy-MM-ddTHH:mm:ss}#{seq}";
            var run = ScheduledRun.Start(recipe.Id, recipe.Name, occurrenceKey);

            var repo = scope.ServiceProvider.GetRequiredService<IScheduledRunRepository>();
            await repo.AddAsync(run, cancellationToken).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var pipeline = scope.ServiceProvider.GetService<IRecipeRunPipeline>();
            if (pipeline == null)
            {
                run.Finish(ScheduledRunStatus.Failed, itemCount: 0, errorClass: "NoPipeline", errorMessage: "Recipe run pipeline is not registered");
                await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new RunNowResult(RunNowOutcome.Started, run);
            }

            // The pipeline finalizes THIS run instance (Finish/MarkInterrupted mutate it
            // in place), so returning it hands the caller the exact, terminal row it
            // started — workspace-ua0c: the run-recipe verb reports this instead of
            // re-querying "latest finished run", which a concurrent scheduler Skipped
            // row (same recipe, past-grace slot) could otherwise win.
            await pipeline.RunAsync(recipe, run, cancellationToken).ConfigureAwait(false);
            return new RunNowResult(RunNowOutcome.Started, run);
        }
        finally
        {
            lease!.Dispose();
        }
    }
}
