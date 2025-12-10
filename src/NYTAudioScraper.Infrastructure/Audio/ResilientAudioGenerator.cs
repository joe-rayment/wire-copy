// Educational and personal use only.

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Configuration;
using Polly;
using Polly.Retry;

namespace NYTAudioScraper.Infrastructure.Audio;

public class ResilientAudioGenerator : IAudioGenerator
{
    private readonly AudioGenerator _primaryGenerator;
    private readonly InworldAudioGenerator? _fallbackGenerator;
    private readonly IBudgetService _budgetService;
    private readonly ILogger<ResilientAudioGenerator> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly bool _fallbackEnabled;

    public ResilientAudioGenerator(
        AudioGenerator primaryGenerator,
        InworldAudioGenerator? fallbackGenerator,
        IOptions<InworldConfiguration> inworldConfig,
        IBudgetService budgetService,
        ILogger<ResilientAudioGenerator> logger)
    {
        _primaryGenerator = primaryGenerator;
        _fallbackGenerator = fallbackGenerator;
        _budgetService = budgetService;
        _logger = logger;
        _fallbackEnabled = inworldConfig.Value.Enabled &&
            !string.IsNullOrEmpty(inworldConfig.Value.ApiKey) &&
            !string.IsNullOrEmpty(inworldConfig.Value.ApiSecret);

        if (_fallbackEnabled)
        {
            _logger.LogInformation("Inworld TTS fallback is enabled");
        }

        // Configure retry policy: 3 retries with exponential backoff
        _retryPolicy = Policy
            .Handle<HttpRequestException>(ex => !IsQuotaExceeded(ex))
            .Or<TaskCanceledException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        exception,
                        "Retry {RetryCount} after {Delay}s due to: {Message}",
                        retryCount,
                        timeSpan.TotalSeconds,
                        exception.Message);
                });
    }

    public async Task<byte[]> GenerateAudioAsync(string text, string voiceId, CancellationToken cancellationToken = default)
    {
        var estimatedCost = EstimateCost(text);

        // Check budget before proceeding
        if (!_budgetService.CanAfford(estimatedCost))
        {
            throw new InvalidOperationException(
                $"Insufficient budget. Estimated cost: ${estimatedCost:F4}, Remaining budget: ${_budgetService.RemainingBudget:F4}");
        }

        try
        {
            // Try primary generator (ElevenLabs) with retry policy
            var audioData = await _retryPolicy.ExecuteAsync(async () =>
                await _primaryGenerator.GenerateAudioAsync(text, voiceId, cancellationToken));

            // Record the expense after successful generation
            _budgetService.RecordExpense(estimatedCost);

            return audioData;
        }
        catch (HttpRequestException ex) when (_fallbackEnabled && ShouldFallback(ex))
        {
            _logger.LogWarning(
                "ElevenLabs failed with {StatusCode}, falling back to Inworld TTS",
                ex.StatusCode);

            return await GenerateWithFallbackAsync(text, voiceId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate audio after retries for text length={Length}", text.Length);
            throw;
        }
    }

    public decimal EstimateCost(string text)
    {
        return _primaryGenerator.EstimateCost(text);
    }

    /// <summary>
    /// Determines if we should fall back to the secondary TTS provider.
    /// </summary>
    private static bool ShouldFallback(HttpRequestException ex)
    {
        // Fall back on quota exceeded, rate limited, or payment required errors
        return ex.StatusCode == HttpStatusCode.TooManyRequests ||
               ex.StatusCode == HttpStatusCode.PaymentRequired ||
               ex.StatusCode == HttpStatusCode.Forbidden ||
               IsQuotaExceeded(ex);
    }

    /// <summary>
    /// Checks if the exception indicates quota/credits exceeded.
    /// </summary>
    private static bool IsQuotaExceeded(HttpRequestException ex)
    {
        // ElevenLabs returns 401 with specific message when credits are exhausted
        // Also check for 429 (rate limit) and 402 (payment required)
        return ex.StatusCode == HttpStatusCode.TooManyRequests ||
               ex.StatusCode == HttpStatusCode.PaymentRequired ||
               (ex.StatusCode == HttpStatusCode.Unauthorized &&
                ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<byte[]> GenerateWithFallbackAsync(
        string text,
        string voiceId,
        CancellationToken cancellationToken)
    {
        if (_fallbackGenerator == null)
        {
            throw new InvalidOperationException("Fallback generator is not configured");
        }

        var fallbackCost = _fallbackGenerator.EstimateCost(text);
        _logger.LogInformation("Using Inworld TTS fallback. Estimated cost: ${Cost:F4}", fallbackCost);

        try
        {
            var audioData = await _fallbackGenerator.GenerateAudioAsync(text, voiceId, cancellationToken);

            // Record the fallback expense
            _budgetService.RecordExpense(fallbackCost);

            return audioData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallback generator also failed for text length={Length}", text.Length);
            throw;
        }
    }
}
