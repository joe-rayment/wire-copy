// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Configuration.Validation;
using WireCopy.Infrastructure.Security;
using WireCopy.Infrastructure.Storage;

namespace WireCopy.Infrastructure.Browser;

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

        services.AddOptions<OpenAiHierarchyConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(OpenAiHierarchyConfiguration.SectionName).Bind(opts));

        // The hierarchy analyzer reuses the OpenAI TTS API key. Bind the
        // section here too so the analyzer resolves cleanly even when
        // AddPodcast() is not on the host (e.g. in tests).
        services.AddOptions<OpenAiTtsConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(OpenAiTtsConfiguration.SectionName).Bind(opts));

        // Register configuration validators
        services.AddSingleton<IValidateOptions<BrowserConfiguration>, BrowserConfigurationValidator>();
        services.AddSingleton<IValidateOptions<OpenAiHierarchyConfiguration>, OpenAiHierarchyConfigurationValidator>();

        // Register Data Protection for cookie encryption
        var dataProtectionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "keys");
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
            .SetApplicationName("WireCopy");

        // Register security and cookie services
        services.AddSingleton<ICookieEncryptionService, DpapiCookieEncryptionService>();
        services.AddSingleton<ICookieManager, CookieManager>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        // Shared CookieContainer — populated at startup and refreshable after browser login
        var sharedCookieContainer = new CookieContainer();
        services.AddSingleton(sharedCookieContainer);
        services.AddSingleton<IHttpCookieRefresher>(sp =>
            new HttpCookieRefresher(
                sp.GetRequiredService<CookieContainer>(),
                sp.GetRequiredService<ICookieManager>(),
                sp.GetRequiredService<ILogger<HttpCookieRefresher>>()));

        // Warm the shared CookieContainer at startup so the HttpClient handler
        // factory below stays fully synchronous and never has to block on
        // LoadCookiesAsync.
        services.AddHostedService<CookieContainerWarmupService>();

        // Register HTTP client for PageLoader with automatic decompression. Cookies
        // are populated separately by CookieContainerWarmupService.
        services.AddHttpClient("BrowserPageLoader")
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var container = sp.GetRequiredService<CookieContainer>();

                return new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 10,
                    UseCookies = true,
                    CookieContainer = container
                };
            });

        // Register browser session (shared Playwright lifecycle)
        services.AddSingleton<BrowserSession>();
        services.AddSingleton<IBrowserSession>(sp => sp.GetRequiredService<BrowserSession>());
        services.AddSingleton<IBrowserSessionControl>(sp => sp.GetRequiredService<BrowserSession>());

        // Register page access priority queue (serializes foreground/background access)
        services.AddSingleton<IPageAccessQueue, PageAccessQueue>();

        // Register auto-login service (uses IServiceScopeFactory for scoped ISiteCredentialRepository)
        services.AddSingleton<IAutoLoginService, AutoLoginService>();

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
                        "WireCopy",
                        "page-cache");
                diskStore = new DiskCacheStore(diskPath, cacheLogger, cacheConfig.Value.MaxDiskSizeBytes);
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
        services.AddSingleton<IHierarchyConfigStore, HierarchyConfigStore>();
        services.AddSingleton<IHierarchyAnalyzer, OpenAiHierarchyAnalyzer>();
        services.AddSingleton<IReadableContentExtractor, ReadableContentExtractor>();
        services.AddSingleton<IArticleLayoutStore, ArticleLayoutStore>();
        services.AddSingleton<ISelectorBasedArticleExtractor, SelectorBasedArticleExtractor>();
        services.AddSingleton<IAiArticleExtractor, OpenAiArticleExtractor>();
        services.AddSingleton<IRssFeedDetector, RssFeedDetector>();

        // workspace-frpl.11 (B8): opportunistic cookie freshness — a headless scheduled
        // load that renders a logged-in page refreshes cookies.json from the foreground
        // session so later runs stay authenticated. Previously the class existed but was
        // unregistered; the HeadlessSectionLoadAdapter consumes it via IAutoCookieRefresher.
        services.AddSingleton<IAutoCookieRefresher>(sp => new AutoCookieRefresher(
            sp.GetRequiredService<IBrowserSession>(),
            sp.GetRequiredService<ICookieManager>(),
            sp.GetRequiredService<IHttpCookieRefresher>(),
            sp.GetRequiredService<IOptions<BrowserConfiguration>>().Value,
            sp.GetRequiredService<NavigationService>(),
            sp.GetRequiredService<ILogger<AutoCookieRefresher>>()));

        // workspace-frpl.3 (B2): durable section resolver for scheduled recipes.
        services.AddSingleton<Application.Interfaces.Scheduling.ISectionResolver, Scheduling.SectionResolver>();

        // Scraping strategies (per-domain WHICH-links chooser).
        services.AddSingleton<IScrapingStrategy, ScrapingStrategies.DocumentOrderStrategy>();
        services.AddSingleton<IScrapingStrategy, ScrapingStrategies.AiCuratedStrategy>();
        services.AddSingleton<IScrapingStrategy, ScrapingStrategies.RssFeedStrategy>();
        services.AddSingleton<IScrapingStrategyRegistry, ScrapingStrategies.ScrapingStrategyRegistry>();

        // Register layout variant provider (cycles visual layout variants per ViewMode)
        services.AddSingleton<ILayoutVariantProvider>(sp =>
        {
            var settingsStore = sp.GetRequiredService<IUserSettingsStore>();
            return new LayoutVariantProvider(settingsStore);
        });

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
            var contentExtractor = sp.GetRequiredService<IReadableContentExtractor>();

            // Article content cache is optional (only available when podcast services are registered).
            // GetService returns null for unregistered services, no try/catch needed.
            var articleCache = sp.GetService<Podcast.Cache.IArticleContentCache>();

            var browserConfig = sp.GetRequiredService<IOptions<BrowserConfiguration>>();

            var cookieManager = sp.GetRequiredService<ICookieManager>();
            var httpCookieRefresher = sp.GetRequiredService<IHttpCookieRefresher>();

            var linkExtractor = sp.GetRequiredService<ILinkExtractor>();

            var browserSession = sp.GetRequiredService<IBrowserSession>();

            return new Cache.BackgroundPreloadService(
                cache,
                idleDetector,
                httpClient,
                cacheConfig.Value,
                preloadLogger,
                contentExtractor,
                linkExtractor,
                articleCache,
                browserConfig.Value,
                cookieManager,
                httpCookieRefresher,
                browserSession);
        });

        // Register page load pipeline (extracted from BrowserOrchestrator)
        services.AddSingleton<PageLoadPipeline>(sp =>
        {
            var pageLoader = sp.GetRequiredService<IPageLoader>();
            var linkExtractor = sp.GetRequiredService<ILinkExtractor>();
            var treeBuilder = sp.GetRequiredService<INavigationTreeBuilder>();
            var contentExtractor = sp.GetRequiredService<IReadableContentExtractor>();
            var feedDetector = sp.GetRequiredService<IRssFeedDetector>();
            var renderer = sp.GetRequiredService<IPageRenderer>();
            var navigationService = sp.GetRequiredService<NavigationService>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var browserSession = sp.GetRequiredService<IBrowserSessionControl>();
            var pageCache = sp.GetRequiredService<IPageCache>();
            var preloadService = sp.GetRequiredService<IPreloadService>();
            var cookieManager = sp.GetRequiredService<ICookieManager>();
            var themeProvider = sp.GetRequiredService<IThemeProvider>();
            var browserConfig = sp.GetRequiredService<IOptions<BrowserConfiguration>>();
            var pipelineLogger = sp.GetRequiredService<ILogger<PageLoadPipeline>>();

            return new PageLoadPipeline(
                pageLoader,
                linkExtractor,
                treeBuilder,
                contentExtractor,
                feedDetector,
                renderer,
                navigationService,
                scopeFactory,
                browserSession,
                pageCache,
                preloadService,
                cookieManager,
                themeProvider,
                browserConfig.Value,
                pipelineLogger);
        });

        // Register the podcast background job manager (workspace-vkhr Phase D).
        // Singleton so detach/reattach across the input loop see the same
        // live job + progress stream.
        services.AddSingleton<IPodcastBackgroundJobManager, PodcastBackgroundJobManager>();

        // Register the main orchestrator
        services.AddSingleton<IBrowserService, BrowserOrchestrator>();

        return services;
    }
}
