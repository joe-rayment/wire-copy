// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Podcast.Chatterbox;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Routes every <see cref="ITtsService"/> call to the engine the TtsEngine setting
/// names — read on EVERY call, never cached, so a Settings change applies to the
/// next call instantly with no restart. Absent/unknown values fall back to OpenAI.
/// Also the <see cref="ITtsCacheKeyProvider"/>: the audio cache partitions on the
/// active engine's full audio-shaping fingerprint.
/// </summary>
internal sealed class TtsEngineRouter : ITtsService, ITtsCacheKeyProvider
{
    private const string ChatterboxEngineName = "chatterbox";

    private readonly OpenAiTtsService _openAi;
    private readonly ChatterboxTtsService _chatterbox;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<TtsEngineRouter> _logger;
    private readonly Lock _fingerprintLock = new();
    private (string Path, DateTime LastWriteUtc, long Length, string Fingerprint)? _sampleFingerprintCache;

    public TtsEngineRouter(
        OpenAiTtsService openAi,
        ChatterboxTtsService chatterbox,
        IUserSettingsStore settingsStore,
        ILogger<TtsEngineRouter> logger)
    {
        _openAi = openAi;
        _chatterbox = chatterbox;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public bool IsConfigured => Active.IsConfigured;

    /// <summary>Gets the engine name the NEXT call will use: "openai" or "chatterbox".</summary>
    internal string ActiveEngineName =>
        _settingsStore.Get(SettingsCommandHandler.KeyTtsEngine)?.Trim().ToLowerInvariant() == ChatterboxEngineName
            ? ChatterboxEngineName
            : "openai";

    /// <summary>Gets the active engine, resolved from the settings store on every access.</summary>
    internal ITtsService Active => ActiveEngineName == ChatterboxEngineName ? _chatterbox : _openAi;

    public TtsCostEstimate EstimateCost(string text) => Active.EstimateCost(text);

    public Task<TtsValidationResult> ValidateApiKeyAsync(CancellationToken cancellationToken = default) =>
        Active.ValidateApiKeyAsync(cancellationToken);

    /// <summary>
    /// ALWAYS forwards to the OpenAI service regardless of the active engine — an API
    /// key is inherently OpenAI's, and the key-entry flow must work even while the
    /// local engine is selected.
    /// </summary>
    public void SetApiKeyOverride(string apiKey) => _openAi.SetApiKeyOverride(apiKey);

    public Task<TtsGenerationResult> GenerateAudioAsync(
        string text,
        string title,
        IProgress<TtsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var engine = ActiveEngineName;
        _logger.LogInformation("TTS generation routed to the {Engine} engine", engine);
        return Active.GenerateAudioAsync(text, title, progress, cancellationToken);
    }

    public string GetTtsConfigCacheComponent()
    {
        if (ActiveEngineName == ChatterboxEngineName)
        {
            var exaggeration = _chatterbox.GetEffectiveExaggeration().ToString(CultureInfo.InvariantCulture);
            var cfgWeight = _chatterbox.GetCfgWeight().ToString(CultureInfo.InvariantCulture);
            return $"chatterbox|english|{SampleFingerprint()}|{exaggeration}|{cfgWeight}";
        }

        var (speed, format) = _openAi.GetStaticAudioParams();
        return string.Join('|',
            "openai",
            _openAi.GetEffectiveVoice(),
            _openAi.GetEffectiveModel(),
            speed.ToString(CultureInfo.InvariantCulture),
            format,
            _openAi.GetEffectiveInstructions() ?? string.Empty);
    }

    /// <summary>
    /// Short content hash of the voice-sample FILE BYTES so swapping the file (even
    /// same-named) re-partitions the cache. Memoized per (path, mtime, length) — the
    /// Settings screen renders every frame and must not re-hash a clip each time.
    /// </summary>
    private string SampleFingerprint()
    {
        var path = _chatterbox.GetConfiguredSamplePath();
        if (path is null)
        {
            return "builtin";
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return "missing";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "missing";
        }

        lock (_fingerprintLock)
        {
            if (_sampleFingerprintCache is { } cached
                && cached.Path == path
                && cached.LastWriteUtc == info.LastWriteTimeUtc
                && cached.Length == info.Length)
            {
                return cached.Fingerprint;
            }
        }

        string fingerprint;
        try
        {
            using var stream = File.OpenRead(path);
            fingerprint = Convert.ToHexString(SHA256.HashData(stream))[..8].ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "missing";
        }

        lock (_fingerprintLock)
        {
            _sampleFingerprintCache = (path, info.LastWriteTimeUtc, info.Length, fingerprint);
        }

        return fingerprint;
    }
}
