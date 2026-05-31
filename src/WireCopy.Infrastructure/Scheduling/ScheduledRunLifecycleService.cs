// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.12 (B10) — startup orphan sweep for scheduled runs, modelled
/// on PodcastJobLifecycleService. A <see cref="WireCopy.Domain.Entities.Scheduling.ScheduledRun"/>
/// left Running when a previous app instance exited is marked Interrupted so it
/// no longer advertises itself as in-flight (and the dedup read doesn't treat a
/// dead run as "currently running"). Best-effort: runs off-thread, never throws
/// into host startup.
/// </summary>
internal sealed class ScheduledRunLifecycleService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledRunLifecycleService> _logger;

    public ScheduledRunLifecycleService(IServiceScopeFactory scopeFactory, ILogger<ScheduledRunLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await SweepOrphanedRunsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Host shutting down before the sweep completed.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scheduled-run orphan sweep failed (non-fatal)");
                }
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SweepOrphanedRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduledRunRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var active = await repo.GetActiveRunsAsync(cancellationToken).ConfigureAwait(false);
        if (active.Count == 0)
        {
            return;
        }

        foreach (var run in active)
        {
            run.MarkInterrupted("App restarted before the scheduled run completed");
            await repo.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "ScheduledRun {RunId} ({Recipe} @ {Occurrence}) marked Interrupted on startup",
                run.Id,
                run.RecipeName,
                run.OccurrenceKey);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
