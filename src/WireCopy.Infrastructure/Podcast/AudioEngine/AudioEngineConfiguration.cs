// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>
/// Defaults for talking to the audio-engine narration service. Every value here can be
/// overridden per call by a user setting (see <c>AudioEngineSettings</c>); the URL and
/// API key can also come from the <c>AUDIO_ENGINE_URL</c> / <c>AUDIO_ENGINE_API_KEY</c>
/// environment variables.
/// </summary>
public sealed class AudioEngineConfiguration
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "AudioEngine";

    /// <summary>Gets the audio-engine base address used when neither a setting nor the environment names one.</summary>
    public string Url { get; init; } = "http://127.0.0.1:8080";

    /// <summary>Gets the OpenAI voice used when the user has not picked one.</summary>
    public string OpenAiVoice { get; init; } = "nova";

    /// <summary>Gets the OpenAI TTS model used when the user has not picked one.</summary>
    public string OpenAiModel { get; init; } = "tts-1";

    /// <summary>Gets the OpenAI speaking-style instructions used when the user has not set any.</summary>
    public string? OpenAiInstructions { get; init; } = "Speak like a playful but knowing news anchor";

    /// <summary>Gets the OpenAI speaking speed (1.0 is normal).</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Gets the Chatterbox exaggeration used when the user has not set one.</summary>
    public double ChatterboxExaggeration { get; init; } = 0.5;
}
