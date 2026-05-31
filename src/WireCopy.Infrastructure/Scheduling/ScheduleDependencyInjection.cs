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
        return services;
    }
}
