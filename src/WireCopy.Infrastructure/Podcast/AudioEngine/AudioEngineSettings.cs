// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser.CommandHandlers;

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>
/// The audio-engine address, key and voice settings in effect at one moment. Resolved fresh from the
/// user settings store on every call so a Settings change applies to the next narration with no restart.
/// </summary>
internal sealed record AudioEngineSettings
{
    /// <summary>The engine id for OpenAI speech.</summary>
    internal const string EngineOpenAi = "openai";

    /// <summary>The engine id for local Chatterbox speech.</summary>
    internal const string EngineChatterbox = "chatterbox";

    /// <summary>Gets the audio-engine base address; null when none is set.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the audio-engine API key; null when none is set.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Gets the engine id, "openai" or "chatterbox".</summary>
    public string Engine { get; init; } = EngineOpenAi;

    /// <summary>Gets the OpenAI voice; null for chatterbox.</summary>
    public string? Voice { get; init; }

    /// <summary>Gets the OpenAI model; null for chatterbox.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the OpenAI speaking-style instructions; null when none.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets the OpenAI speaking speed; null for chatterbox.</summary>
    public double? Speed { get; init; }

    /// <summary>Gets the Chatterbox exaggeration; null for openai.</summary>
    public double? Exaggeration { get; init; }

    /// <summary>Gets the absolute path of the Chatterbox voice sample, when one is saved and exists.</summary>
    public string? VoiceSamplePath { get; init; }

    /// <summary>Gets a value indicating whether the engine is chatterbox.</summary>
    public bool IsChatterbox => Engine == EngineChatterbox;

    /// <summary>
    /// Reads the current settings: the user's saved values first, then the environment, then the defaults
    /// in <paramref name="config"/>. Cheap: no hashing and no network.
    /// </summary>
    /// <param name="store">The user settings store.</param>
    /// <param name="config">The configured defaults.</param>
    /// <param name="getEnv">Reads an environment variable.</param>
    /// <returns>The settings in effect now.</returns>
    public static AudioEngineSettings Resolve(IUserSettingsStore store, AudioEngineConfiguration config, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(getEnv);

        var url = Clean(store.Get("AudioEngineUrl")) ?? Clean(getEnv("AUDIO_ENGINE_URL")) ?? Clean(config.Url);
        var apiKey = Clean(store.Get("AudioEngineApiKey")) ?? Clean(getEnv("AUDIO_ENGINE_API_KEY"));

        var chatterbox = string.Equals(
            store.Get(SettingsCommandHandler.KeyTtsEngine)?.Trim(),
            EngineChatterbox,
            StringComparison.OrdinalIgnoreCase);

        if (chatterbox)
        {
            return new AudioEngineSettings
            {
                Url = url,
                ApiKey = apiKey,
                Engine = EngineChatterbox,
                Exaggeration = ResolveExaggeration(store, config),
                VoiceSamplePath = ResolveVoiceSample(store),
            };
        }

        // An instructions entry that exists but is blank means "no instructions"; an absent one means the default.
        var savedInstructions = store.Get(SettingsCommandHandler.KeyOpenAiTtsInstructions);
        var instructions = savedInstructions is null ? Clean(config.OpenAiInstructions) : Clean(savedInstructions);

        return new AudioEngineSettings
        {
            Url = url,
            ApiKey = apiKey,
            Engine = EngineOpenAi,
            Voice = Clean(store.Get(SettingsCommandHandler.KeyOpenAiTtsVoice)) ?? config.OpenAiVoice,
            Model = Clean(store.Get(SettingsCommandHandler.KeyOpenAiTtsModel)) ?? config.OpenAiModel,
            Instructions = instructions,
            Speed = config.Speed,
        };
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static double ResolveExaggeration(IUserSettingsStore store, AudioEngineConfiguration config)
    {
        var saved = store.Get(SettingsCommandHandler.KeyChatterboxExaggeration);
        if (saved is not null
            && double.TryParse(saved.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            return Math.Clamp(parsed, 0.0, 2.0);
        }

        return config.ChatterboxExaggeration;
    }

    private static string? ResolveVoiceSample(IUserSettingsStore store)
    {
        var saved = Clean(store.Get(SettingsCommandHandler.KeyChatterboxVoiceSample));
        if (saved is null)
        {
            return null;
        }

        var full = Path.GetFullPath(saved, Environment.CurrentDirectory);
        return File.Exists(full) ? full : null;
    }
}
