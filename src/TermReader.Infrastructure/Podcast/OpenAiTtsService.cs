// Educational and personal use only.

using System.ClientModel;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Audio;
using TermReader.Application.DTOs;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Text-to-speech service backed by the OpenAI TTS API.
/// </summary>
internal sealed class OpenAiTtsService : ITtsService
{
    private const decimal CostPerMillionChars = 15.00m;
    private const double WordsPerMinute = 150.0;
    private const double AverageCharsPerWord = 5.0;

    private readonly OpenAiTtsConfiguration _config;
    private readonly ILogger<OpenAiTtsService> _logger;
    private string? _apiKeyOverride;

    public OpenAiTtsService(IOptions<OpenAiTtsConfiguration> config, ILogger<OpenAiTtsService> logger)
    {
        _config = config.Value;
        _logger = logger;
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
        var costUsd = charCount * CostPerMillionChars / 1_000_000m;
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
        var audioClient = CreateAudioClient();
        var options = CreateSpeechOptions();
        var voice = MapVoice(_config.Voice);

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

            var chunkAudio = await GenerateChunkWithRetryAsync(
                audioClient, chunk, voice, options, i + 1, chunks.Count, cancellationToken);

            if (chunkAudio == null)
            {
                return TtsGenerationResult.Failure(
                    $"Failed to generate audio for chunk {i + 1}/{chunks.Count} after {_config.MaxRetries} retries.",
                    totalCharsProcessed,
                    i);
            }

            allAudioSegments.Add(chunkAudio);
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
                await Task.Delay(_config.InterChunkDelayMs, cancellationToken);
            }
        }

        var concatenated = allAudioSegments.Count == 1
            ? allAudioSegments[0]
            : await ConcatenateWithFfmpegAsync(allAudioSegments, cancellationToken);

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
            var voice = MapVoice(_config.Voice);

            // Minimal TTS call (~4 chars, cost ~$0.00006) to validate the full pipeline.
            var result = await audioClient.GenerateSpeechAsync("test", voice, options, cancellationToken);

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

    private async Task<byte[]?> GenerateChunkWithRetryAsync(
        AudioClient audioClient,
        string chunk,
        GeneratedSpeechVoice voice,
        SpeechGenerationOptions options,
        int chunkNumber,
        int totalChunks,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                var result = await audioClient.GenerateSpeechAsync(chunk, voice, options, cancellationToken);
                return result.Value.ToArray();
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
                    return null;
                }

                var delayMs = _config.RetryBaseDelayMs * (int)Math.Pow(2, attempt);
                _logger.LogWarning(
                    "Chunk {ChunkNumber}/{TotalChunks} attempt {Attempt} failed (HTTP {Status}), retrying in {DelayMs}ms",
                    chunkNumber,
                    totalChunks,
                    attempt + 1,
                    ex.Status,
                    delayMs);

                await Task.Delay(delayMs, cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status is 400 or 401 or 403)
            {
                // Non-retryable client errors
                _logger.LogError(
                    ex,
                    "Chunk {ChunkNumber}/{TotalChunks} failed with non-retryable error (HTTP {Status})",
                    chunkNumber,
                    totalChunks,
                    ex.Status);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Chunk {ChunkNumber}/{TotalChunks} failed with unexpected error: {Message}",
                    chunkNumber,
                    totalChunks,
                    ex.Message);
                return null;
            }
        }

        return null;
    }

    private AudioClient CreateAudioClient()
    {
        var apiKey = GetEffectiveApiKey()!;
        return new AudioClient(_config.Model, new ApiKeyCredential(apiKey));
    }

    private SpeechGenerationOptions CreateSpeechOptions()
    {
        var options = new SpeechGenerationOptions
        {
            SpeedRatio = _config.Speed,
            ResponseFormat = MapFormat(_config.OutputFormat),
        };

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

        return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    #pragma warning disable OPENAI001 // Experimental voices
    private static GeneratedSpeechVoice MapVoice(string voice)
    {
        return voice.ToLowerInvariant() switch
        {
            "alloy" => GeneratedSpeechVoice.Alloy,
            "ash" => GeneratedSpeechVoice.Ash,
            "ballad" => GeneratedSpeechVoice.Ballad,
            "coral" => GeneratedSpeechVoice.Coral,
            "echo" => GeneratedSpeechVoice.Echo,
            "fable" => GeneratedSpeechVoice.Fable,
            "onyx" => GeneratedSpeechVoice.Onyx,
            "nova" => GeneratedSpeechVoice.Nova,
            "sage" => GeneratedSpeechVoice.Sage,
            "shimmer" => GeneratedSpeechVoice.Shimmer,
            _ => GeneratedSpeechVoice.Nova,
        };
    }
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
            await File.WriteAllBytesAsync(chunkPath, segments[i], cancellationToken);
            chunkPaths.Add(chunkPath);
        }

        var outputPath = tempManager.GetTempFilePath($"concatenated{ext}");

        await FFMpegArguments
            .FromDemuxConcatInput(chunkPaths)
            .OutputToFile(outputPath, overwrite: true, options => options
                .WithCustomArgument("-c copy"))
            .ProcessAsynchronously(throwOnError: true);

        return await File.ReadAllBytesAsync(outputPath, cancellationToken);
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
}
