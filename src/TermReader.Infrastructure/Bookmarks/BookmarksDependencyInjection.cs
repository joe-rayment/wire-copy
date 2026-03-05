// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using TermReader.Application.Interfaces;
using TermReader.Persistence.Repositories;

namespace TermReader.Infrastructure.Bookmarks;

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
        services.AddScoped<IBookmarkService, BookmarkService>();
        return services;
    }
}
