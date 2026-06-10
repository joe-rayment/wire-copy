// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Demo;
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

        // workspace-kt19.2: the loopback server behind the shipped demo
        // bookmarks (no-op when the pack is absent or no bookmark targets it).
        services.AddHostedService<DemoSiteHostedService>();
        return services;
    }
}
