// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Audio;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Configuration.Validation;
using TermReader.Infrastructure.Podcast.Cache;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Extension methods for registering podcast services.
/// </summary>
public static class PodcastDependencyInjection
{
    /// <summary>
    /// Adds podcast services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPodcast(this IServiceCollection services)
    {
        // Register configuration
        services.AddOptions<OpenAiTtsConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(OpenAiTtsConfiguration.SectionName).Bind(opts));

        services.AddOptions<PodcastConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(PodcastConfiguration.SectionName).Bind(opts));

        services.AddOptions<GcsConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(GcsConfiguration.SectionName).Bind(opts));

        services.AddOptions<TtsAudioCacheConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(TtsAudioCacheConfiguration.SectionName).Bind(opts));

        // Register configuration validators
        services.AddSingleton<IValidateOptions<OpenAiTtsConfiguration>, OpenAiTtsConfigurationValidator>();
        services.AddSingleton<IValidateOptions<PodcastConfiguration>, PodcastConfigurationValidator>();
        services.AddSingleton<IValidateOptions<GcsConfiguration>, GcsConfigurationValidator>();
        services.AddSingleton<IValidateOptions<TtsAudioCacheConfiguration>, TtsAudioCacheConfigurationValidator>();

        // Register user settings store (must be before services that consume it)
        services.AddSingleton<IUserSettingsStore, UserSettingsStore>();

        // Register services
        services.AddSingleton<ITtsService, OpenAiTtsService>();
        services.AddSingleton<IAudioAssembler, M4bAudioAssembler>();
        services.AddSingleton<IPodcastFeedGenerator, PodcastFeedGenerator>();
        services.AddSingleton<ICloudStorageClient, GcsStorageClient>();
        services.AddSingleton<IPodcastPublisher, PodcastPublisher>();
        services.AddSingleton<ITtsAudioCache, FileSystemTtsAudioCache>();
        services.AddSingleton<IArticleContentCache, ArticleContentCache>();
        services.AddSingleton<ReadingListContentProvider>();
        services.AddSingleton<OutputFolderPurger>();
        services.AddHostedService<OutputFolderPurgeStartupService>();
        services.AddSingleton<IPodcastOrchestrator, PodcastOrchestrator>();

        return services;
    }
}
