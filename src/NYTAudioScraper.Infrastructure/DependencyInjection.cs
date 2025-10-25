using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Audio;
using NYTAudioScraper.Infrastructure.Browser;
using NYTAudioScraper.Infrastructure.Configuration;
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

        // Register services
        services.AddSingleton<IScraperService, ScraperService>();
        services.AddSingleton<IAudioGenerator, AudioGenerator>();
        services.AddSingleton<IAudioProcessor, AudioProcessor>();
        services.AddSingleton<IChapterMarker, ChapterMarker>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        return services;
    }
}
