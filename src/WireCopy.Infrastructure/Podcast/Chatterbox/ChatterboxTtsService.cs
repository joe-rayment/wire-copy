// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Podcast.Chatterbox;

/// <summary>
/// Local narration engine: a drop-in <see cref="ITtsService"/> over the Chatterbox
/// sidecar. $0 cost, no API key; tone comes from the user's voice sample plus the
/// Expressiveness (exaggeration) setting — Chatterbox has no text-instructions channel.
/// Every failure string starts with the sentinel prefix "Local narration: " (the
/// podcast failure classifier keys on it); the only exception is the shared
/// "Audio concatenation failed:" copy.
/// </summary>
internal sealed class ChatterboxTtsService : ITtsService
{
    private const string Sentinel = "Local narration: ";
    private const double WordsPerMinute = 150.0;
    private const double AverageCharsPerWord = 5.0;
    private static readonly TimeSpan UvProbeTtl = TimeSpan.FromSeconds(60);

    private readonly IChatterboxSidecar _sidecar;
    private readonly ChatterboxConfiguration _config;
    private readonly ILogger<ChatterboxTtsService> _logger;
    private readonly IUserSettingsStore? _settingsStore;
    private (bool Available, DateTime ProbedAtUtc)? _uvProbe;

    public ChatterboxTtsService(
        IChatterboxSidecar sidecar,
        IOptions<ChatterboxConfiguration> config,
        ILogger<ChatterboxTtsService> logger,
        IUserSettingsStore? settingsStore = null)
    {
        _sidecar = sidecar;
        _config = config.Value;
        _logger = logger;
        _settingsStore = settingsStore;
    }

    public bool IsConfigured => UvAvailable() && File.Exists(GetWorkerPath());

    public TtsCostEstimate EstimateCost(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var chunks = TextChunker.ChunkText(text, _config.MaxChunkSize);

        return new TtsCostEstimate
        {
            CharacterCount = text.Length,
            ChunkCount = chunks.Count,
            EstimatedCostUsd = 0m,
            EstimatedDurationMinutes = text.Length / (WordsPerMinute * AverageCharsPerWord),
        };
    }

    /// <summary>
    /// Local-engine readiness check. NO process spawn, NO downloads — that heaviness
    /// belongs to the Test-narration action. First failure wins.
    /// </summary>
    public Task<TtsValidationResult> ValidateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!UvAvailable())
        {
            return Task.FromResult(TtsValidationResult.Invalid(
                "uv is not installed — install it with: curl -LsSf https://astral.sh/uv/install.sh | sh",
                "uv_missing"));
        }

        var workerPath = GetWorkerPath();
        if (!File.Exists(workerPath))
        {
            return Task.FromResult(TtsValidationResult.Invalid(
                $"Chatterbox worker script not found at {workerPath} — rebuild the app.",
                "worker_missing"));
        }

        var configuredSample = GetConfiguredSamplePath();
        if (configuredSample is not null && !File.Exists(configuredSample))
        {
            return Task.FromResult(TtsValidationResult.Invalid(
                $"Voice sample not found: {configuredSample}",
                "sample_missing"));
        }

        return Task.FromResult(TtsValidationResult.Valid());
    }

    public void SetApiKeyOverride(string apiKey)
    {
        // API keys are an OpenAI concept; the local engine has none.
        _logger.LogDebug("SetApiKeyOverride ignored — the Chatterbox engine takes no API key");
    }

    public async Task<TtsGenerationResult> GenerateAudioAsync(
        string text,
        string title,
        IProgress<TtsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(title);

        if (!IsConfigured)
        {
            var reason = !UvAvailable()
                ? "uv is not installed — install it with: curl -LsSf https://astral.sh/uv/install.sh | sh"
                : $"Chatterbox worker script not found at {GetWorkerPath()} — rebuild the app.";
            return TtsGenerationResult.Failure(Sentinel + "engine not ready — " + reason);
        }

        var chunks = TextChunker.ChunkText(text, _config.MaxChunkSize);
        var samplePath = ResolveSamplePath();
        var exaggeration = GetEffectiveExaggeration();

        _logger.LogInformation(
            "Starting local TTS generation for '{Title}': {Chars} chars, {Chunks} chunks, sample={Sample}, exaggeration={Exaggeration}",
            title,
            text.Length,
            chunks.Count,
            samplePath ?? "(built-in voice)",
            exaggeration);

        // Sidecar/setup lines (uv env build, model download) surface through the
        // podcast progress screen so the first run shows activity instead of freezing.
        var setupProgress = progress is null
            ? null
            : new Progress<string>(line => progress.Report(new TtsProgress
            {
                CurrentChunk = 0,
                TotalChunks = chunks.Count,
                CharactersProcessed = 0,
                TotalCharacters = text.Length,
                Message = line,
            }));

        using var tempManager = new TempFileManager(null, _logger);
        var wavPaths = new List<string>(chunks.Count);
        var totalCharsProcessed = 0;

        try
        {
            await _sidecar.StartAsync(setupProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chatterbox sidecar failed to start");
            return TtsGenerationResult.Failure(Sentinel + ex.Message);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outPath = tempManager.GetTempFilePath($"chunk-{i:D4}.wav");
            var request = new ChatterboxSpeakRequest($"chunk-{i}", chunks[i], samplePath, exaggeration, _config.CfgWeight, outPath);

            var error = await SpeakChunkAsync(request, setupProgress, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                // One recovery attempt: restart the worker and retry the same chunk once.
                // No exponential backoff — this is a local process, not a rate-limited API.
                _logger.LogWarning("Chunk {Chunk}/{Total} failed ({Error}) — restarting the worker for one retry", i + 1, chunks.Count, error);
                await _sidecar.StopAsync().ConfigureAwait(false);
                try
                {
                    await _sidecar.StartAsync(setupProgress, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return TtsGenerationResult.Failure(Sentinel + ex.Message, totalCharsProcessed, i);
                }

                error = await SpeakChunkAsync(request, setupProgress, cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    return TtsGenerationResult.Failure(Sentinel + error, totalCharsProcessed, i);
                }
            }

            totalCharsProcessed += chunks[i].Length;
            wavPaths.Add(outPath);

            progress?.Report(new TtsProgress
            {
                CurrentChunk = i + 1,
                TotalChunks = chunks.Count,
                CharactersProcessed = totalCharsProcessed,
                TotalCharacters = text.Length,
                Message = $"Chunk {i + 1}/{chunks.Count} complete",
            });
        }

        byte[] aacBytes;
        try
        {
            aacBytes = await AssembleToAacAsync(tempManager, wavPaths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "FFmpeg assembly of Chatterbox wavs failed: {Message}", ex.Message);
            return TtsGenerationResult.Failure(
                $"Audio concatenation failed: {ex.Message}",
                totalCharsProcessed,
                chunks.Count);
        }

        _logger.LogInformation(
            "Local TTS generation complete for '{Title}': {Chars} chars, {Chunks} chunks, {Bytes} bytes",
            title,
            totalCharsProcessed,
            chunks.Count,
            aacBytes.Length);

        return TtsGenerationResult.Successful(aacBytes, totalCharsProcessed, chunks.Count);
    }

#pragma warning disable SA1202 // ordering — internal seams placed near the private helpers they wrap
    /// <summary>
    /// Effective exaggeration: the ChatterboxExaggeration settings key (invariant
    /// float) wins over the bound default; garbage/absent falls back; clamped to [0, 2].
    /// </summary>
    internal float GetEffectiveExaggeration()
    {
        var saved = _settingsStore?.Get(SettingsCommandHandler.KeyChatterboxExaggeration);
        if (!string.IsNullOrWhiteSpace(saved)
            && float.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !float.IsNaN(parsed)
            && !float.IsInfinity(parsed))
        {
            return Math.Clamp(parsed, 0f, 2f);
        }

        return Math.Clamp(_config.DefaultExaggeration, 0f, 2f);
    }

    /// <summary>
    /// The configured voice-sample path resolved to absolute (relative paths resolve
    /// against the current directory — the repo root under ./run), WITHOUT an
    /// existence check. Null when unset/blank (built-in voice).
    /// </summary>
    internal string? GetConfiguredSamplePath()
    {
        var saved = _settingsStore?.Get(SettingsCommandHandler.KeyChatterboxVoiceSample);
        if (string.IsNullOrWhiteSpace(saved))
        {
            return null;
        }

        return Path.GetFullPath(saved.Trim(), Environment.CurrentDirectory);
    }

    /// <summary>
    /// The sample path generation should use: null when unset OR when the configured
    /// file is missing (generation proceeds with the built-in voice; the Settings row
    /// is where hard validation lives).
    /// </summary>
    internal string? ResolveSamplePath()
    {
        var configured = GetConfiguredSamplePath();
        if (configured is null)
        {
            return null;
        }

        if (!File.Exists(configured))
        {
            _logger.LogWarning("Voice sample not found at {Path} — generating with the built-in voice", configured);
            return null;
        }

        return configured;
    }

    /// <summary>Worker script location in the app's build output.</summary>
    internal string GetWorkerPath() => Path.Combine(AppContext.BaseDirectory, _config.WorkerRelativePath);
#pragma warning restore SA1202

    /// <summary>
    /// Concatenates the per-chunk WAVs and transcodes ONCE to AAC so the return
    /// contract matches OpenAI's (the audio cache stores .aac; the M4B assembler
    /// re-encodes at assembly, so 24 kHz mono AAC is safe). The single-chunk case
    /// still transcodes — wav in, aac out, always.
    /// </summary>
    private static async Task<byte[]> AssembleToAacAsync(TempFileManager tempManager, List<string> wavPaths, CancellationToken ct)
    {
        var outPath = tempManager.GetTempFilePath("narration.m4a");

        await FFMpegArguments
            .FromDemuxConcatInput(wavPaths)
            .OutputToFile(outPath, overwrite: true, options => options
                .WithCustomArgument("-c:a aac -b:a 96k -ar 24000 -ac 1"))
            .ProcessAsynchronously(throwOnError: true).ConfigureAwait(false);

        return await File.ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one speak request; returns null on success, the error text on failure.</summary>
    private async Task<string?> SpeakChunkAsync(ChatterboxSpeakRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            var result = await _sidecar.SpeakAsync(request, progress, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                return result.Error ?? "the worker reported an unspecified error";
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private bool UvAvailable()
    {
        // The Settings screen polls IsConfigured every render frame — cache the
        // filesystem/PATH probe for a minute.
        if (_uvProbe is { } probe && DateTime.UtcNow - probe.ProbedAtUtc < UvProbeTtl)
        {
            return probe.Available;
        }

        var available = ResolveUvExists();
        _uvProbe = (available, DateTime.UtcNow);
        return available;
    }

    private bool ResolveUvExists()
    {
        var uvPath = _config.UvPath;
        if (string.IsNullOrWhiteSpace(uvPath))
        {
            return false;
        }

        if (uvPath.Contains(Path.DirectorySeparatorChar) || uvPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(uvPath);
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, uvPath);
            if (File.Exists(candidate) || (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")))
            {
                return true;
            }
        }

        return false;
    }
}
