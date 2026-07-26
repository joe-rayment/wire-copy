// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Persistence.Repositories;

namespace WireCopy.Persistence;

/// <summary>
/// Extension methods for registering persistence services.
/// </summary>
public static class PersistenceDependencyInjection
{
    /// <summary>
    /// Adds persistence services (DbContext, repositories, UnitOfWork) to the service collection.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, string? connectionString = null)
    {
        connectionString ??= $"Data Source={Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "wirecopy.db")}";

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connectionString));

        // workspace-xpr5t: one signal per host, so the orphan sweeps can wait for the
        // migration instead of racing it and logging SQLite "no such table" errors on a
        // first run. Registered here because it belongs to the same lifetime as the
        // DbContext it describes.
        services.AddSingleton<DatabaseReadySignal>();

        services.AddSingleton<ICollectionPreferences, PersistentCollectionPreferences>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISiteCredentialRepository, SiteCredentialRepository>();
        services.AddScoped<IPodcastJobRepository, PodcastJobRepository>();
        services.AddScoped<Application.Interfaces.Scheduling.IScheduledRunRepository, ScheduledRunRepository>();

        return services;
    }
}
