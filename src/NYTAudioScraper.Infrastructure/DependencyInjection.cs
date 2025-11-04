using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Audio;
using NYTAudioScraper.Infrastructure.Browser;
using NYTAudioScraper.Infrastructure.Configuration;
using NYTAudioScraper.Infrastructure.Parsing;
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
        .AddTransientHttpErrorPolicy(policyBuilder =>
            policyBuilder.WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    var logger = context.GetLogger();
                    logger?.LogWarning(
                        "Retry {RetryCount} after {Delay}s due to {Exception}",
                        retryCount,
                        timespan.TotalSeconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "Unknown");
                }))
        .AddTransientHttpErrorPolicy(policyBuilder =>
            policyBuilder.CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (outcome, breakDuration) =>
                {
                    // Log circuit breaker opened
                    Console.WriteLine($"Circuit breaker opened for {breakDuration.TotalSeconds}s due to {outcome.Exception?.Message ?? "repeated failures"}");
                },
                onReset: () =>
                {
                    // Log circuit breaker closed
                    Console.WriteLine("Circuit breaker reset - service is healthy again");
                },
                onHalfOpen: () =>
                {
                    // Log circuit breaker testing
                    Console.WriteLine("Circuit breaker half-open - testing if service recovered");
                }));

        // Register browser automation services
        services.AddSingleton<NYTAuthService>();
        services.AddSingleton<ArticleParser>();

        // Register audio services with resilience
        services.AddSingleton<BudgetService>();
        services.AddSingleton<AudioGenerator>();
        services.AddSingleton<IAudioGenerator, ResilientAudioGenerator>();

        // Register services
        services.AddSingleton<IScraperService, ScraperService>();
        services.AddSingleton<IAudioProcessor, AudioProcessor>();
        services.AddSingleton<IChapterMarker, ChapterMarker>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        return services;
    }
}
