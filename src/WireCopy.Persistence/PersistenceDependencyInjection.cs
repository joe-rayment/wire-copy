// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.Interfaces;
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

        services.AddSingleton<ICollectionPreferences, PersistentCollectionPreferences>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISiteCredentialRepository, SiteCredentialRepository>();

        return services;
    }
}
