// Educational and personal use only.

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces.Browser;
using NYTAudioScraper.Infrastructure.Browser.UI;
using NYTAudioScraper.Infrastructure.Configuration;

namespace NYTAudioScraper.Infrastructure.Browser;

/// <summary>
/// Extension methods for registering terminal browser services.
/// </summary>
public static class BrowserDependencyInjection
{
    /// <summary>
    /// Adds terminal browser services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTerminalBrowser(this IServiceCollection services)
    {
        // Register HTTP client for PageLoader with automatic decompression
        services.AddHttpClient("BrowserPageLoader")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            });

        // Register browser infrastructure services
        services.AddSingleton<IPageLoader>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("BrowserPageLoader");
            var browserConfig = sp.GetRequiredService<IOptions<BrowserConfiguration>>();
            var logger = sp.GetRequiredService<ILogger<PageLoader>>();
            return new PageLoader(browserConfig, logger, httpClient);
        });
        services.AddSingleton<ILinkExtractor, LinkExtractor>();
        services.AddSingleton<INavigationTreeBuilder, NavigationTreeBuilder>();
        services.AddSingleton<IReadableContentExtractor, ReadableContentExtractor>();

        // Register navigation service (manages history and state)
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

        // Register UI components
        services.AddSingleton<IPageRenderer, TerminalPageRenderer>();
        services.AddSingleton<IInputHandler, TerminalInputHandler>();

        // Register the main orchestrator
        services.AddSingleton<IBrowserService, BrowserOrchestrator>();

        return services;
    }
}
