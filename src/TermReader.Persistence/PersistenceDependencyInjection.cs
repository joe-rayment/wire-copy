// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TermReader.Application.Interfaces;
using TermReader.Persistence.Repositories;

namespace TermReader.Persistence;

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
            "TermReader",
            "termreader.db")}";

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<IBookmarkRepository, BookmarkRepository>();

        return services;
    }
}
