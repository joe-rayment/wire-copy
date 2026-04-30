// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.Interfaces;
using WireCopy.Persistence.Repositories;

namespace WireCopy.Infrastructure.Bookmarks;

/// <summary>
/// Extension methods for registering bookmark services.
/// </summary>
public static class BookmarksDependencyInjection
{
    /// <summary>
    /// Adds bookmark services to the service collection.
    /// </summary>
    public static IServiceCollection AddBookmarks(this IServiceCollection services)
    {
        services.AddScoped<IBookmarkRepository, BookmarkRepository>();
        services.AddSingleton<IBookmarkConfigStore, JsonBookmarkConfigStore>();
        services.AddScoped<IBookmarkReconciler, BookmarkReconciler>();
        services.AddScoped<IBookmarkService, BookmarkService>();
        return services;
    }
}
