// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Audio;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Configuration.Validation;
using WireCopy.Infrastructure.Podcast.Cache;
using WireCopy.Infrastructure.Podcast.Chatterbox;

namespace WireCopy.Infrastructure.Podcast;

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

        services.AddOptions<ChatterboxConfiguration>()
            .Configure<IConfiguration>((opts, config) =>
                config.GetSection(ChatterboxConfiguration.SectionName).Bind(opts));

        // Register configuration validators
        services.AddSingleton<IValidateOptions<OpenAiTtsConfiguration>, OpenAiTtsConfigurationValidator>();
        services.AddSingleton<IValidateOptions<PodcastConfiguration>, PodcastConfigurationValidator>();
        services.AddSingleton<IValidateOptions<GcsConfiguration>, GcsConfigurationValidator>();
        services.AddSingleton<IValidateOptions<TtsAudioCacheConfiguration>, TtsAudioCacheConfigurationValidator>();
        services.AddSingleton<IValidateOptions<ChatterboxConfiguration>, ChatterboxConfigurationValidator>();

        // Register user settings store (must be before services that consume it)
        services.AddSingleton<IUserSettingsStore, UserSettingsStore>();

        // Register services. ITtsService is the ENGINE ROUTER — it re-reads the
        // TtsEngine setting on every call, so a Settings switch applies instantly.
        services.AddSingleton<OpenAiTtsService>();
        services.AddSingleton<IChatterboxSidecar, ChatterboxSidecar>();
        services.AddSingleton<ChatterboxTtsService>();
        services.AddSingleton<TtsEngineRouter>();
        services.AddSingleton<ITtsService>(sp => sp.GetRequiredService<TtsEngineRouter>());
        services.AddSingleton<ITtsCacheKeyProvider>(sp => sp.GetRequiredService<TtsEngineRouter>());
        services.AddSingleton<IAudioAssembler, M4bAudioAssembler>();
        services.AddSingleton<IPodcastFeedGenerator, PodcastFeedGenerator>();
        services.AddSingleton<ICloudStorageClient, GcsStorageClient>();
        services.AddSingleton<IFeedReachabilityProbe, HttpFeedReachabilityProbe>();
        services.AddSingleton<IPodcastPublisher, PodcastPublisher>();
        services.AddSingleton<ITtsAudioCache, FileSystemTtsAudioCache>();
        services.AddSingleton<IArticleContentCache, ArticleContentCache>();
        services.AddSingleton<ReadingListContentProvider>();
        services.AddSingleton<OutputFolderPurger>();
        services.AddHostedService<OutputFolderPurgeStartupService>();
        services.AddHostedService<PodcastJobLifecycleService>();
        services.AddSingleton<IPodcastOrchestrator, PodcastOrchestrator>();

        // workspace-frpl.1 (B0): one process-wide generation gate shared by the
        // manual modal and the scheduler so podcast runs never overlap.
        services.AddSingleton<IPodcastGenerationGate, PodcastGenerationGate>();

        return services;
    }
}
