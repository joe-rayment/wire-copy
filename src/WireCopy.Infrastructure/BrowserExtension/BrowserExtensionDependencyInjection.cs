// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>Extension methods for registering the browser-extension support services.</summary>
public static class BrowserExtensionDependencyInjection
{
    /// <summary>Adds the startup service that registers the extension's native-messaging host.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddBrowserExtensionSupport(this IServiceCollection services)
    {
        services.AddHostedService<BrowserExtensionHostedService>();
        return services;
    }
}
