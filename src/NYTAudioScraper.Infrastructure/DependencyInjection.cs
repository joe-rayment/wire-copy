// <copyright file="DependencyInjection.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Audio;
using NYTAudioScraper.Infrastructure.Browser;
using NYTAudioScraper.Infrastructure.Caching;
using NYTAudioScraper.Infrastructure.Configuration;
using NYTAudioScraper.Infrastructure.Configuration.Validation;
using NYTAudioScraper.Infrastructure.Health;
using NYTAudioScraper.Infrastructure.Http;
using NYTAudioScraper.Infrastructure.Parsing;
using NYTAudioScraper.Infrastructure.Persistence;
using NYTAudioScraper.Infrastructure.Persistence.Repositories;
using NYTAudioScraper.Infrastructure.Security;
using NYTAudioScraper.Infrastructure.Storage;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace NYTAudioScraper.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure services
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register configuration
        services.Configure<NYTConfiguration>(options =>
            configuration.GetSection(NYTConfiguration.SectionName).Bind(options));
        services.Configure<ElevenLabsConfiguration>(options =>
            configuration.GetSection(ElevenLabsConfiguration.SectionName).Bind(options));
        services.Configure<AudioConfiguration>(options =>
            configuration.GetSection(AudioConfiguration.SectionName).Bind(options));
        services.Configure<BrowserConfiguration>(options =>
            configuration.GetSection(BrowserConfiguration.SectionName).Bind(options));

        // Register configuration validators
        services.AddSingleton<IValidateOptions<NYTConfiguration>, NYTConfigurationValidator>();
        services.AddSingleton<IValidateOptions<ElevenLabsConfiguration>, ElevenLabsConfigurationValidator>();
        services.AddSingleton<IValidateOptions<AudioConfiguration>, AudioConfigurationValidator>();
        services.AddSingleton<IValidateOptions<BrowserConfiguration>, BrowserConfigurationValidator>();

        // Validate options on startup
        services.AddOptions<NYTConfiguration>().ValidateOnStart();
        services.AddOptions<ElevenLabsConfiguration>().ValidateOnStart();
        services.AddOptions<AudioConfiguration>().ValidateOnStart();
        services.AddOptions<BrowserConfiguration>().ValidateOnStart();

        // Register database
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NYTAudioScraper", "nytaudioscraper.db");

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={connectionString}"));

        // Register Unit of Work
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Register repositories
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IScrapingSessionRepository, ScrapingSessionRepository>();

        // Register HTTP client factory
        services.AddHttpClient();

        // Register HTTP resilience logger
        services.AddSingleton<HttpResilienceLogger>();

        // Register ElevenLabs HTTP client with configuration and resilience policies
        services.AddHttpClient("ElevenLabs", (serviceProvider, client) =>
        {
            var config = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ElevenLabsConfiguration>>().Value;
            client.BaseAddress = new Uri(config.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(5); // Generous timeout for audio generation

            if (!string.IsNullOrEmpty(config.ApiKey))
            {
                client.DefaultRequestHeaders.Add("xi-api-key", config.ApiKey);
            }
        })
        .AddTransientHttpErrorPolicy(policyBuilder =>
        {
            return policyBuilder.WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
        })
        .AddTransientHttpErrorPolicy(policyBuilder =>
        {
            return policyBuilder.CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30));
        });

        // Register Data Protection for cookie encryption
        var dataProtectionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NYTAudioScraper",
            "keys");
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
            .SetApplicationName("NYTAudioScraper");

        // Register security services
        services.AddSingleton<ICookieEncryptionService, DpapiCookieEncryptionService>();
        services.AddSingleton<ICookieManager, CookieManager>();

        // Register browser automation services
        services.AddSingleton<INYTAuthService, NYTAuthService>();
        services.AddSingleton<IArticleParser, ArticleParser>();

        // Register audio services with resilience
        services.AddSingleton<IBudgetService, BudgetService>();
        services.AddSingleton<AudioGenerator>();
        services.AddSingleton<IAudioGenerator, ResilientAudioGenerator>();

        // Register rate limiter for parallel processing
        // Max 3 concurrent requests, 1000ms minimum delay between requests
        services.AddSingleton<IRateLimiter>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<RateLimiter>>();
            return new RateLimiter(
                maxConcurrency: 3,
                minDelayMs: 1000,
                logger);
        });

        // Register parallel audio generator
        services.AddSingleton<IParallelAudioGenerator, ParallelAudioGenerator>();

        // Register caching
        services.AddMemoryCache();
        services.AddScoped<IArticleCache, ArticleCache>();
        services.AddSingleton<IAudioCache>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<AudioCache>>();
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NYTAudioScraper",
                "cache",
                "audio");
            return new AudioCache(cacheDirectory, logger);
        });

        // Register health checks
        services.AddHealthChecks()
            .AddCheck<FFmpegHealthCheck>("ffmpeg", tags: new[] { "ready" })
            .AddCheck<DiskSpaceHealthCheck>("disk_space", tags: new[] { "ready" })
            .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

        // Register services
        services.AddScoped<IScraperService, ScraperService>(); // Scoped because it depends on IArticleCache (which uses DbContext)
        services.AddSingleton<IAudioProcessor, AudioProcessor>();
        services.AddSingleton<IChapterMarker, ChapterMarker>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<CookieImporter>(); // Singleton for cookie management

        return services;
    }
}
