using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Audio;
using NYTAudioScraper.Infrastructure.Browser;
using NYTAudioScraper.Infrastructure.Caching;
using NYTAudioScraper.Infrastructure.Configuration;
using NYTAudioScraper.Infrastructure.Parsing;
using NYTAudioScraper.Infrastructure.Persistence;
using NYTAudioScraper.Infrastructure.Persistence.Repositories;
using NYTAudioScraper.Infrastructure.Storage;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using System.Net.Http;

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

        // Register database
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NYTAudioScraper", "nytaudioscraper.db");

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={connectionString}"));

        // Register repositories
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IScrapingSessionRepository, ScrapingSessionRepository>();

        // Register HTTP client factory
        services.AddHttpClient();

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
        .AddTransientHttpErrorPolicy((serviceProvider, policyBuilder) =>
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("NYTAudioScraper.Infrastructure.Http.RetryPolicy");
            return policyBuilder.WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    var reason = outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "Unknown";
                    logger.LogWarning(
                        "ElevenLabs API retry {RetryCount} after {Delay}s due to {Reason}",
                        retryCount,
                        timespan.TotalSeconds,
                        reason);
                });
        })
        .AddTransientHttpErrorPolicy((serviceProvider, policyBuilder) =>
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("NYTAudioScraper.Infrastructure.Http.CircuitBreaker");
            return policyBuilder.CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (outcome, breakDuration) =>
                {
                    var reason = outcome.Exception?.Message ?? "repeated failures";
                    logger.LogError(
                        "ElevenLabs API circuit breaker opened for {Duration}s due to {Reason}",
                        breakDuration.TotalSeconds,
                        reason);
                },
                onReset: () =>
                {
                    logger.LogInformation("ElevenLabs API circuit breaker reset - service is healthy again");
                },
                onHalfOpen: () =>
                {
                    logger.LogInformation("ElevenLabs API circuit breaker half-open - testing if service recovered");
                });
        });

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

        // Register services
        services.AddSingleton<IScraperService, ScraperService>();
        services.AddSingleton<IAudioProcessor, AudioProcessor>();
        services.AddSingleton<IChapterMarker, ChapterMarker>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        return services;
    }
}
