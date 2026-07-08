// Licensed under the MIT License. See LICENSE in the repository root.

using System.ClientModel;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Audio;
using WireCopy.Application.DTOs;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Text-to-speech service backed by the OpenAI TTS API.
/// </summary>
internal sealed class OpenAiTtsService : ITtsService
{
    // Per-model $/1M input characters. tts-1-hd is documented at ~2x tts-1 (the model picker's
    // own copy says so); gpt-4o-mini-tts (the default) is priced ≈$0.015/min of audio, which at
    // ~150 wpm * 5 chars/word = 750 chars/min works out to ≈$20/1M chars. A flat $15 under-gated
    // the MaxBudgetUsd check by 2x for tts-1-hd and under-reported the default model.
    private const decimal CostPerMillionCharsTts1 = 15.00m;
    private const decimal CostPerMillionCharsTts1Hd = 30.00m;
    private const decimal CostPerMillionCharsMiniTts = 20.00m;
    private const double WordsPerMinute = 150.0;
    private const double AverageCharsPerWord = 5.0;

    private readonly OpenAiTtsConfiguration _config;
    private readonly IUserSettingsStore? _settingsStore;
    private readonly ILogger<OpenAiTtsService> _logger;
    private string? _apiKeyOverride;

    public OpenAiTtsService(
        IOptions<OpenAiTtsConfiguration> config,
        ILogger<OpenAiTtsService> logger,
        IUserSettingsStore? settingsStore = null)
    {
        _config = config.Value;
        _logger = logger;
        _settingsStore = settingsStore;
    }

    public bool IsConfigured
    {
        get
        {
            var key = GetEffectiveApiKey();
            return !string.IsNullOrWhiteSpace(key);
        }
    }

    public TtsCostEstimate EstimateCost(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var charCount = text.Length;
        var chunkCount = (int)Math.Ceiling((double)charCount / _config.MaxChunkSize);
        var costUsd = charCount * GetEffectiveCostPerMillionChars() / 1_000_000m;
        var estimatedMinutes = charCount / (WordsPerMinute * AverageCharsPerWord);

        return new TtsCostEstimate
        {
            CharacterCount = charCount,
            ChunkCount = chunkCount,
            EstimatedCostUsd = costUsd,
            EstimatedDurationMinutes = estimatedMinutes,
        };
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
            return TtsGenerationResult.Failure("TTS service is not configured. Set OpenAiTts:ApiKey or OPENAI_API_KEY.");
        }

        var estimate = EstimateCost(text);
        if (estimate.EstimatedCostUsd > _config.MaxBudgetUsd)
        {
            return TtsGenerationResult.Failure(
                $"Estimated cost ${estimate.EstimatedCostUsd:F4} exceeds budget limit ${_config.MaxBudgetUsd:F2}.");
        }

        _logger.LogInformation(
            "Starting TTS generation for '{Title}': {Summary}",
            title,
            estimate.Summary);

        var chunks = TextChunker.ChunkText(text, _config.MaxChunkSize);

        // workspace-xott: the SDK (2.12.0) now exposes SpeechGenerationOptions.Instructions, so
        // EVERY chunk goes through the SDK — the raw-HTTP fallback that carried the instructions
        // field is gone. CreateSpeechOptions sets Instructions from GetEffectiveInstructions().
        var audioClient = CreateAudioClient();
        var options = CreateSpeechOptions();
        var voice = MapVoice(GetEffectiveVoice());

        var allAudioSegments = new List<byte[]>(chunks.Count);
        var totalCharsProcessed = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = chunks[i];

            _logger.LogDebug(
                "Processing chunk {ChunkIndex}/{TotalChunks} ({ChunkLength} chars)",
                i + 1,
                chunks.Count,
                chunk.Length);

            ChunkGenerationResult chunkResult;

            // workspace-rz1c: a retry handler that surfaces the transient
            // 429/5xx wait via the IProgress<TtsProgress> stream so the
            // podcast progress screen can render a "Rate-limited, retrying
            // in Xs" banner instead of appearing frozen during exponential
            // backoff. Captures i + 1 / chunks.Count so the consumer can
            // colocate the retry banner with the in-flight chunk.
            void OnRetry(int attempt, int delayMs, int status)
            {
                progress?.Report(BuildRetryProgress(
                    attempt,
                    delayMs,
                    status,
                    chunkIndex: i + 1,
                    totalChunks: chunks.Count,
                    charactersProcessed: totalCharsProcessed,
                    totalCharacters: text.Length,
                    maxRetries: _config.MaxRetries));
            }

            chunkResult = await GenerateChunkWithRetryAsync(
                audioClient, chunk, voice, options, i + 1, chunks.Count, OnRetry, cancellationToken).ConfigureAwait(false);

            if (!chunkResult.Success || chunkResult.AudioData is null)
            {
                var errorMessage = chunkResult.ErrorMessage ?? $"Chunk {i + 1}/{chunks.Count} failed without an error message.";
                return TtsGenerationResult.Failure(errorMessage, totalCharsProcessed, i);
            }

            allAudioSegments.Add(chunkResult.AudioData);
            totalCharsProcessed += chunk.Length;

            progress?.Report(new TtsProgress
            {
                CurrentChunk = i + 1,
                TotalChunks = chunks.Count,
                CharactersProcessed = totalCharsProcessed,
                TotalCharacters = text.Length,
                Message = $"Chunk {i + 1}/{chunks.Count} complete",
            });

            // Inter-chunk delay for rate limiting (skip after last chunk)
            if (i < chunks.Count - 1 && _config.InterChunkDelayMs > 0)
            {
                await Task.Delay(_config.InterChunkDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        byte[] concatenated;
        try
        {
            concatenated = allAudioSegments.Count == 1
                ? allAudioSegments[0]
                : await ConcatenateWithFfmpegAsync(allAudioSegments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "FFmpeg concatenation failed: {Message}", ex.Message);
            return TtsGenerationResult.Failure(
                $"Audio concatenation failed: {ex.Message}",
                totalCharsProcessed,
                chunks.Count);
        }

        _logger.LogInformation(
            "TTS generation complete for '{Title}': {Chars} chars, {Chunks} chunks, {Bytes} bytes",
            title,
            totalCharsProcessed,
            chunks.Count,
            concatenated.Length);

        return TtsGenerationResult.Successful(concatenated, totalCharsProcessed, chunks.Count);
    }

    /// <inheritdoc />
    public async Task<TtsValidationResult> ValidateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return TtsValidationResult.Invalid("No API key configured.", "no_key");
        }

        try
        {
            var audioClient = CreateAudioClient();
            var options = CreateSpeechOptions();
            var voice = MapVoice(GetEffectiveVoice());

            // Minimal TTS call (~4 chars, cost ~$0.00006) to validate the full pipeline.
            var result = await audioClient.GenerateSpeechAsync("test", voice, options, cancellationToken).ConfigureAwait(false);

            // Discard audio bytes — we only care that the call succeeded.
            _ = result.Value;

            _logger.LogInformation("API key validation succeeded");
            return TtsValidationResult.Valid();
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            _logger.LogWarning("API key validation failed: invalid key (HTTP 401)");
            return TtsValidationResult.Invalid("Invalid API key.", "invalid_key");
        }
        catch (ClientResultException ex) when (ex.Status == 403)
        {
            _logger.LogWarning("API key validation failed: insufficient permissions (HTTP 403)");
            return TtsValidationResult.Invalid(
                "API key lacks permissions or account has insufficient credits.", "insufficient_credits");
        }
        catch (ClientResultException ex) when (ex.Status == 400)
        {
            // workspace-xott follow-up: the validation call now carries the user's real voice /
            // model / instructions through the SDK (the voice is forwarded VERBATIM), so a typo'd
            // voice or bad combination surfaces here as a 400. The key itself is fine — point the
            // user at their TTS settings and include the API's message (it names the bad value and
            // lists the supported ones) instead of mislabelling this a key/network failure.
            _logger.LogWarning("API key validation: bad request (HTTP 400) — TTS settings invalid, key likely valid: {Message}", ex.Message);
            return TtsValidationResult.Invalid(
                $"Key looks valid, but the TTS settings were rejected (check voice/model): {ex.Message}", "bad_request");
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("API key validation: rate limited (HTTP 429) — key is likely valid");
            return TtsValidationResult.Invalid(
                "Rate limited — key is valid but try again shortly.", "rate_limited");
        }
        catch (ClientResultException ex) when (ex.Status >= 500)
        {
            _logger.LogWarning(ex, "API key validation: server error (HTTP {Status})", ex.Status);
            return TtsValidationResult.Invalid(
                "OpenAI service error — key may be valid, try again.", "server_error");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API key validation failed with unexpected error: {Message}", ex.Message);
            return TtsValidationResult.Invalid(
                $"Validation failed: {ex.Message}", "network_error");
        }
    }

    /// <summary>
    /// Allows runtime injection of an API key from the UI prompt.
    /// </summary>
    public void SetApiKeyOverride(string apiKey)
    {
        _apiKeyOverride = apiKey;
    }

    /// <summary>
    /// workspace-rz1c: builds the <see cref="TtsProgress"/> emitted just
    /// before each backoff sleep so the consumer can render a "rate-limited,
    /// retrying in Xs (attempt N/M)" banner. Extracted to a static helper so
    /// the formatter is testable without a real <see cref="OpenAiTtsService"/>
    /// + HTTP/SDK seam.
    /// </summary>
    internal static TtsProgress BuildRetryProgress(
        int attempt,
        int delayMs,
        int status,
        int chunkIndex,
        int totalChunks,
        int charactersProcessed,
        int totalCharacters,
        int maxRetries)
    {
        var delaySeconds = Math.Max(1, (int)Math.Ceiling(delayMs / 1000.0));
        var message = status switch
        {
            429 => $"Rate-limited by OpenAI (HTTP 429), retrying in {delaySeconds}s (attempt {attempt}/{maxRetries})",

            // workspace-roab: status==0 signals a network-layer transient
            // (DNS failure, connection refused, no status code from the
            // server). Surface it with copy distinct from the HTTP-status
            // path so the user knows the issue is connectivity, not auth.
            0 => $"Network issue, retrying in {delaySeconds}s (attempt {attempt}/{maxRetries})",
            _ => $"OpenAI returned HTTP {status}, retrying in {delaySeconds}s (attempt {attempt}/{maxRetries})",
        };

        return new TtsProgress
        {
            CurrentChunk = chunkIndex,
            TotalChunks = totalChunks,
            CharactersProcessed = charactersProcessed,
            TotalCharacters = totalCharacters,
            IsRetrying = true,
            RetryAttempt = attempt,
            RetryMaxAttempts = maxRetries,
            RetryDelaySeconds = delaySeconds,
            Message = message,
        };
    }

    // workspace-xott: GeneratedSpeechVoice is an extensible enum, so we forward the configured
    // voice string to the SDK VERBATIM (falling back to a sane default only when blank). This
    // matches the removed raw-HTTP path, which POSTed the raw voice string — so voices the SDK's
    // named members don't enumerate (e.g. "marin"/"cedar", valid per the API's own 400 message)
    // still reach the API instead of being silently coerced to Nova. The API validates the value
    // (a bad voice surfaces as 400). Internal for tests, like the GetEffective* seams.
#pragma warning disable OPENAI001 // GeneratedSpeechVoice members/ctor are an experimental OpenAI API surface — scoped to exactly this helper
    internal static GeneratedSpeechVoice MapVoice(string voice) =>
        string.IsNullOrWhiteSpace(voice)
            ? GeneratedSpeechVoice.Nova
            : new GeneratedSpeechVoice(voice.Trim().ToLowerInvariant());
#pragma warning restore OPENAI001

    private static GeneratedSpeechFormat MapFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "mp3" => GeneratedSpeechFormat.Mp3,
            "opus" => GeneratedSpeechFormat.Opus,
            "aac" => GeneratedSpeechFormat.Aac,
            "flac" => GeneratedSpeechFormat.Flac,
            "wav" => GeneratedSpeechFormat.Wav,
            "pcm" => GeneratedSpeechFormat.Pcm,
            _ => GeneratedSpeechFormat.Aac,
        };
    }

    private async Task<ChunkGenerationResult> GenerateChunkWithRetryAsync(
        AudioClient audioClient,
        string chunk,
        GeneratedSpeechVoice voice,
        SpeechGenerationOptions options,
        int chunkNumber,
        int totalChunks,
        Action<int, int, int>? onRetry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                var result = await audioClient.GenerateSpeechAsync(chunk, voice, options, cancellationToken).ConfigureAwait(false);
                return ChunkGenerationResult.Ok(result.Value.ToArray());
            }
            catch (ClientResultException ex) when (ex.Status == 429 || ex.Status >= 500)
            {
                if (attempt == _config.MaxRetries)
                {
                    _logger.LogError(
                        ex,
                        "Chunk {ChunkNumber}/{TotalChunks} failed after {Attempts} attempts (HTTP {Status})",
                        chunkNumber,
                        totalChunks,
                        attempt + 1,
                        ex.Status);
                    var kind = ex.Status == 429 ? "rate limited" : "server error";
                    return ChunkGenerationResult.Fail(
                        $"Failed to generate audio for chunk {chunkNumber}/{totalChunks} after {_config.MaxRetries} retries ({kind}).");
                }

                var delayMs = _config.RetryBaseDelayMs * (int)Math.Pow(2, attempt);
                _logger.LogWarning(
                    "Chunk {ChunkNumber}/{TotalChunks} attempt {Attempt} failed (HTTP {Status}), retrying in {DelayMs}ms",
                    chunkNumber,
                    totalChunks,
                    attempt + 1,
                    ex.Status,
                    delayMs);

                // workspace-rz1c: surface the retry on the progress stream so
                // the user sees "Rate-limited, retrying in Xs" instead of a
                // frozen progress bar during the backoff sleep.
                onRetry?.Invoke(attempt + 1, delayMs, ex.Status);

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (ClientResultException ex) when (ex.Status is 401 or 403)
            {
                _logger.LogError(
                    ex,
                    "Chunk {ChunkNumber}/{TotalChunks} failed with auth error (HTTP {Status})",
                    chunkNumber,
                    totalChunks,
                    ex.Status);
                return ChunkGenerationResult.Fail(
                    $"Authentication failed for chunk {chunkNumber}/{totalChunks} \u2014 check your API key.");
            }
            catch (ClientResultException ex) when (ex.Status == 400)
            {
                _logger.LogError(
                    ex,
                    "Chunk {ChunkNumber}/{TotalChunks} failed with bad request (HTTP 400)",
                    chunkNumber,
                    totalChunks);
                return ChunkGenerationResult.Fail(
                    $"Bad request for chunk {chunkNumber}/{totalChunks}: {ex.Message}");
            }
            catch (Exception ex) when (IsSdkTransportFailure(ex))
            {
                // workspace-roab: transport-layer transients (DNS lookup
                // failed, connection refused, TLS handshake error). The SDK
                // (System.ClientModel) never lets HttpRequestException escape:
                // its transport rethrows ClientResultException with Status 0,
                // and its internal retry policy throws AggregateException when
                // it exhausts its own attempts — so those are the shapes we
                // must handle here; the old bare-HttpRequestException filter
                // was dead code, so network blips used to fall through to the
                // generic no-retry handler. Retry with the same backoff as 429/5xx, surface
                // "Network issue" copy via onRetry(0) so the user sees
                // connectivity-specific messaging instead of frozen.
                if (attempt == _config.MaxRetries)
                {
                    _logger.LogError(
                        ex,
                        "Chunk {ChunkNumber}/{TotalChunks} failed after {Attempts} network attempts: {Message}",
                        chunkNumber,
                        totalChunks,
                        attempt + 1,
                        ex.Message);
                    return ChunkGenerationResult.Fail(
                        $"Network error generating chunk {chunkNumber}/{totalChunks} after {_config.MaxRetries} retries: {ex.Message}");
                }

                var delayMs = _config.RetryBaseDelayMs * (int)Math.Pow(2, attempt);
                _logger.LogWarning(
                    "Chunk {ChunkNumber}/{TotalChunks} attempt {Attempt} failed (network): {Message}, retrying in {DelayMs}ms",
                    chunkNumber,
                    totalChunks,
                    attempt + 1,
                    ex.Message,
                    delayMs);

                onRetry?.Invoke(attempt + 1, delayMs, 0);

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Chunk {ChunkNumber}/{TotalChunks} failed with unexpected error: {Message}",
                    chunkNumber,
                    totalChunks,
                    ex.Message);
                return ChunkGenerationResult.Fail(
                    $"Unexpected error generating chunk {chunkNumber}/{totalChunks}: {ex.Message}");
            }
        }

        return ChunkGenerationResult.Fail(
            $"Failed to generate audio for chunk {chunkNumber}/{totalChunks} after {_config.MaxRetries} retries.");
    }

    /// <summary>
    /// True when <paramref name="ex"/> is a connectivity-shaped failure from the OpenAI SDK —
    /// never an HTTP status from the API. System.ClientModel's transport catches
    /// <see cref="HttpRequestException"/> and rethrows <see cref="ClientResultException"/> with
    /// Status 0, and its internal retry policy throws <see cref="AggregateException"/> on
    /// exhaustion ("Retry failed after N tries"). The bare HttpRequestException arm is kept as
    /// a defensive net only. Cancellation inside an aggregate is NOT retryable.
    /// </summary>
#pragma warning disable SA1204 // static helper kept adjacent to its sole caller (GenerateChunkWithRetryAsync)
    private static bool IsSdkTransportFailure(Exception ex) => ex switch
    {
        ClientResultException { Status: 0 } => true,
        HttpRequestException { StatusCode: null } => true,
        AggregateException agg => !agg.Flatten().InnerExceptions.Any(e => e is OperationCanceledException),
        _ => false,
    };
#pragma warning restore SA1204

    private AudioClient CreateAudioClient()
    {
        var apiKey = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI TTS API key is not configured.");
        }

        return new AudioClient(GetEffectiveModel(), new ApiKeyCredential(apiKey));
    }

#pragma warning disable SA1202 // ordering — internal test helpers placed near the private callees they wrap
    /// <summary>
    /// Resolves the effective TTS voice. Settings store wins over the bound config
    /// so the user's runtime selection in the Generate Podcast confirmation screen
    /// takes effect immediately without restart.
    /// </summary>
    internal string GetEffectiveVoice()
    {
        var saved = _settingsStore?.Get("OpenAiTtsVoice");
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return saved;
        }

        return _config.Voice;
    }

    /// <summary>
    /// Resolves the effective TTS model (tts-1, tts-1-hd, etc.). Settings store wins
    /// over the bound config so the user's runtime selection takes effect immediately.
    /// </summary>
    internal string GetEffectiveModel()
    {
        var saved = _settingsStore?.Get("OpenAiTtsModel");
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return saved;
        }

        return _config.Model;
    }

    /// <summary>
    /// Resolves the effective TTS style instructions. Settings store wins over the
    /// bound config; null/empty resolves to null so callers can omit the
    /// <c>instructions</c> field on the request entirely (sending an empty string
    /// would still be interpreted by the API).
    ///
    /// Note: only the <c>gpt-4o-mini-tts</c> family acts on this — older
    /// <c>tts-1</c> / <c>tts-1-hd</c> models silently ignore it. We let the user
    /// configure it regardless and rely on the model field at request time.
    /// </summary>
    internal string? GetEffectiveInstructions()
    {
        // A PRESENT saved value always wins — including the EMPTY string, which is the
        // ":set instructions none" disable sentinel (SettingsCommandHandler: "persists an empty
        // string so the request omits instructions"). Treating empty as unset made 'none' fall
        // back to the bound default, so the playful-news-anchor style could never be turned off.
        var saved = _settingsStore?.Get("OpenAiTtsInstructions");
        if (saved is not null)
        {
            return string.IsNullOrWhiteSpace(saved) ? null : saved;
        }

        return string.IsNullOrWhiteSpace(_config.Instructions) ? null : _config.Instructions;
    }

    /// <summary>Bound (non-overridable) audio parameters for the engine-aware cache key —
    /// speed and output format have no settings-store override, so the config values ARE
    /// the effective ones (workspace-2xej.5, consumed by TtsEngineRouter).</summary>
    internal (float Speed, string Format) GetStaticAudioParams() => (_config.Speed, _config.OutputFormat);

    /// <summary>Rate for the EFFECTIVE model (settings override wins), so the budget gate
    /// and the confirmation screen's estimate track the model the run will actually use.</summary>
    internal decimal GetEffectiveCostPerMillionChars() => GetEffectiveModel().Trim().ToLowerInvariant() switch
    {
        "tts-1" => CostPerMillionCharsTts1,
        "tts-1-hd" => CostPerMillionCharsTts1Hd,
        _ => CostPerMillionCharsMiniTts, // gpt-4o-mini-tts family (the shipped default)
    };
#pragma warning restore SA1202

    /// <summary>
    /// Builds the SDK <see cref="SpeechGenerationOptions"/> for every TTS chunk. The style
    /// <c>Instructions</c> (workspace-xott: exposed by OpenAI SDK 2.12.0, still an experimental
    /// OPENAI001 API surface — only the <c>gpt-4o-mini-tts</c> family acts on it; older models
    /// ignore it) are set from <see cref="GetEffectiveInstructions"/>, which returns null when
    /// unset so the field is simply omitted.
    /// </summary>
    private SpeechGenerationOptions CreateSpeechOptions()
    {
        var options = new SpeechGenerationOptions
        {
            SpeedRatio = _config.Speed,
            ResponseFormat = MapFormat(_config.OutputFormat),
        };

#pragma warning disable OPENAI001 // Instructions is an experimental OpenAI API surface (gpt-4o-mini-tts style control)
        options.Instructions = GetEffectiveInstructions();
#pragma warning restore OPENAI001

        return options;
    }

    private string? GetEffectiveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_apiKeyOverride))
        {
            return _apiKeyOverride;
        }

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            return _config.ApiKey;
        }

        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey;
        }

        // Check persisted settings (saved via :set apikey) as final fallback.
        // When found, promote to runtime override so subsequent calls are instant.
        var savedKey = _settingsStore?.Get("OpenAiApiKey");
        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            _apiKeyOverride = savedKey;
            return savedKey;
        }

        return null;
    }

    /// <summary>
    /// Concatenates multiple audio segments using FFmpeg's concat demuxer.
    /// Raw byte concatenation is invalid for container formats like M4A/AAC;
    /// FFmpeg properly re-muxes the streams into a single valid file.
    /// </summary>
    private async Task<byte[]> ConcatenateWithFfmpegAsync(
        List<byte[]> segments,
        CancellationToken cancellationToken)
    {
        using var tempManager = new TempFileManager(null, _logger);

        var ext = GetFileExtension();
        var chunkPaths = new List<string>(segments.Count);

        for (var i = 0; i < segments.Count; i++)
        {
            var chunkPath = tempManager.GetTempFilePath($"chunk-{i:D4}{ext}");
            await File.WriteAllBytesAsync(chunkPath, segments[i], cancellationToken).ConfigureAwait(false);
            chunkPaths.Add(chunkPath);
        }

        var outputPath = tempManager.GetTempFilePath($"concatenated{ext}");

        await FFMpegArguments
            .FromDemuxConcatInput(chunkPaths)
            .OutputToFile(outputPath, overwrite: true, options => options
                .WithCustomArgument("-c copy"))
            .ProcessAsynchronously(throwOnError: true).ConfigureAwait(false);

        return await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
    }

    private string GetFileExtension()
    {
        return _config.OutputFormat.ToLowerInvariant() switch
        {
            "mp3" => ".mp3",
            "opus" => ".ogg",
            "aac" => ".m4a",
            "flac" => ".flac",
            "wav" => ".wav",
            "pcm" => ".pcm",
            _ => ".m4a",
        };
    }

    private readonly record struct ChunkGenerationResult(bool Success, byte[]? AudioData, string? ErrorMessage)
    {
        public static ChunkGenerationResult Ok(byte[] audioData) => new(true, audioData, null);

        public static ChunkGenerationResult Fail(string errorMessage) => new(false, null, errorMessage);
    }
}
