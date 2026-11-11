// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces;

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>
/// The audio-engine address and key in effect at one moment. Resolved fresh from the
/// user settings store on every call so a Settings change applies to the next narration with no restart.
/// </summary>
internal sealed record AudioEngineSettings
{
    /// <summary>Environment variable that supplies the audio-engine API key when none is saved.</summary>
    internal const string ApiKeyEnvVar = "AUDIO_ENGINE_API_KEY";

    /// <summary>Environment variable that supplies the audio-engine URL when none is saved.</summary>
    internal const string UrlEnvVar = "AUDIO_ENGINE_URL";

    /// <summary>Gets the audio-engine base address; null when none is set.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the audio-engine API key; null when none is set.</summary>
    public string? ApiKey { get; init; }

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

        var url = Clean(store.Get("AudioEngineUrl")) ?? Clean(getEnv(UrlEnvVar)) ?? Clean(config.Url);
        var apiKey = Clean(store.Get("AudioEngineApiKey")) ?? Clean(getEnv(ApiKeyEnvVar));

        return new AudioEngineSettings { Url = url, ApiKey = apiKey };
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
