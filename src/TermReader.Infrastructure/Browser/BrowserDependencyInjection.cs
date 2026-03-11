// Educational and personal use only.

using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Configuration.Validation;
using TermReader.Infrastructure.Security;
using TermReader.Infrastructure.Storage;

namespace TermReader.Infrastructure.Browser;

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
        // Register configuration
        services.AddOptions<BrowserConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(BrowserConfiguration.SectionName).Bind(opts));

        // Register configuration validators
        services.AddSingleton<IValidateOptions<BrowserConfiguration>, BrowserConfigurationValidator>();

        // Register Data Protection for cookie encryption
        var dataProtectionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermReader",
            "keys");
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
            .SetApplicationName("TermReader");

        // Register security and cookie services
        services.AddSingleton<ICookieEncryptionService, DpapiCookieEncryptionService>();
        services.AddSingleton<ICookieManager, CookieManager>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

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

        // Register browser session (shared WebDriver lifecycle)
        services.AddSingleton<BrowserSession>();
        services.AddSingleton<IBrowserSession>(sp => sp.GetRequiredService<BrowserSession>());
        services.AddSingleton<IBrowserSessionControl>(sp => sp.GetRequiredService<BrowserSession>());

        // Register cache configuration and services
        services.AddOptions<CacheConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(CacheConfiguration.SectionName).Bind(opts));
        services.AddSingleton<IValidateOptions<CacheConfiguration>, CacheConfigurationValidator>();
        services.AddSingleton<IPageCache>(sp =>
        {
            var cacheConfig = sp.GetRequiredService<IOptions<CacheConfiguration>>();
            var cacheLogger = sp.GetRequiredService<ILogger<InMemoryPageCache>>();

            DiskCacheStore? diskStore = null;
            if (cacheConfig.Value.DiskCacheEnabled)
            {
                var diskPath = cacheConfig.Value.DiskCachePath
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "TermReader",
                        "page-cache");
                diskStore = new DiskCacheStore(diskPath, cacheLogger);
            }

            return new InMemoryPageCache(cacheConfig, cacheLogger, diskStore);
        });

        // Register browser infrastructure services with caching decorator
        services.AddSingleton<IPageLoader>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("BrowserPageLoader");
            var browserConfig = sp.GetRequiredService<IOptions<BrowserConfiguration>>();
            var pageLoaderLogger = sp.GetRequiredService<ILogger<PageLoader>>();
            var browserSession = sp.GetRequiredService<IBrowserSession>();
            var innerLoader = new PageLoader(browserConfig, pageLoaderLogger, browserSession, httpClient);

            var cache = sp.GetRequiredService<IPageCache>();
            var cachingLogger = sp.GetRequiredService<ILogger<CachingPageLoader>>();
            return new CachingPageLoader(innerLoader, cache, cachingLogger);
        });
        services.AddSingleton<ILinkExtractor, LinkExtractor>();
        services.AddSingleton<INavigationTreeBuilder, NavigationTreeBuilder>();
        services.AddSingleton<IReadableContentExtractor, ReadableContentExtractor>();

        // Register navigation service (manages history and state)
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

        // Register theme provider
        services.AddSingleton<IThemeProvider, ThemeProvider>();

        // Register UI components
        services.AddSingleton<IPageRenderer, TerminalPageRenderer>();
        services.AddSingleton<IInputHandler, TerminalInputHandler>();
        services.AddSingleton<IResizeDetector, TerminalResizeDetector>();

        // Register idle detection and pre-loading services
        services.AddSingleton<IIdleDetector>(sp =>
        {
            var cacheConfig = sp.GetRequiredService<IOptions<CacheConfiguration>>();
            return new Cache.InputIdleDetector(cacheConfig);
        });
        services.AddSingleton<IPreloadService>(sp =>
        {
            var cache = sp.GetRequiredService<IPageCache>();
            var idleDetector = sp.GetRequiredService<IIdleDetector>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("BrowserPageLoader");
            var cacheConfig = sp.GetRequiredService<IOptions<CacheConfiguration>>();
            var preloadLogger = sp.GetRequiredService<ILogger<Cache.BackgroundPreloadService>>();
            return new Cache.BackgroundPreloadService(
                cache, idleDetector, httpClient, cacheConfig.Value, preloadLogger);
        });

        // Register the main orchestrator
        services.AddSingleton<IBrowserService, BrowserOrchestrator>();

        return services;
    }
}
