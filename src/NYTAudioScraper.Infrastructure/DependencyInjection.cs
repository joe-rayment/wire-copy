using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Audio;
using NYTAudioScraper.Infrastructure.Browser;
using NYTAudioScraper.Infrastructure.Configuration;
using NYTAudioScraper.Infrastructure.Parsing;
using NYTAudioScraper.Infrastructure.Storage;

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

        // Register ElevenLabs HTTP client with configuration
        services.AddHttpClient("ElevenLabs", (serviceProvider, client) =>
        {
            var config = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ElevenLabsConfiguration>>().Value;
            client.BaseAddress = new Uri(config.BaseUrl);

            if (!string.IsNullOrEmpty(config.ApiKey))
            {
                client.DefaultRequestHeaders.Add("xi-api-key", config.ApiKey);
            }
        });

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
