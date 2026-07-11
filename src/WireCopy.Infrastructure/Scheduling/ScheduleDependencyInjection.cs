// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>workspace-frpl.5 — DI registration for the scheduling subsystem.</summary>
public static class ScheduleDependencyInjection
{
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
        services.AddSingleton<IScheduleStore>(sp =>
            new ScheduleStore(storageDirectory: null, sp.GetService<ILogger<ScheduleStore>>()));

        // workspace-frpl.12 (B10): startup orphan sweep for scheduled runs.
        services.AddHostedService<ScheduledRunLifecycleService>();

        // workspace-frpl.6 (B5): unattended (no-TUI, still headful) section loader
        // for scheduled runs.
        services.AddSingleton<IUnattendedSectionLoader, UnattendedSectionLoadAdapter>();

        // workspace-frpl.8 (B7): the per-occurrence run pipeline. SCOPED so it shares
        // the same EF unit-of-work as the scheduler's per-tick scope (the scope that
        // wrote the Running row also finalizes it).
        services.AddScoped<IRecipeRunPipeline, RecipeRunPipeline>();

        // workspace-frpl.14 (B12a): user-triggered "run now" from the Schedules screen,
        // reusing the scheduler's gate + Running-row admission protocol.
        services.AddSingleton<IScheduleRunNow, ScheduleRunNow>();

        // workspace-frpl.7 (B6): the in-process scheduler tick loop.
        services.AddHostedService<SchedulerHostedService>();
        return services;
    }
}
