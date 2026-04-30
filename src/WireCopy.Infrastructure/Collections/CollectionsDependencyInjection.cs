// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.Interfaces;
using WireCopy.Persistence.Repositories;

namespace WireCopy.Infrastructure.Collections;

/// <summary>
/// Extension methods for registering collection services.
/// </summary>
public static class CollectionsDependencyInjection
{
    /// <summary>
    /// Adds collection services to the service collection.
    /// </summary>
    public static IServiceCollection AddCollections(this IServiceCollection services)
    {
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<ICollectionService, CollectionService>();
        services.AddSingleton<ICollectionExporter, UrlListExporter>();
        services.AddSingleton<ICollectionExporter, OpmlExporter>();
        return services;
    }
}
