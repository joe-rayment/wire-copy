// <copyright file="ResilientAudioGenerator.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using Polly;
using Polly.Retry;

namespace NYTAudioScraper.Infrastructure.Audio;

public class ResilientAudioGenerator : IAudioGenerator
{
    private readonly AudioGenerator _innerGenerator;
    private readonly IBudgetService _budgetService;
    private readonly ILogger<ResilientAudioGenerator> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;

    public ResilientAudioGenerator(
        AudioGenerator innerGenerator,
        IBudgetService budgetService,
        ILogger<ResilientAudioGenerator> logger)
    {
        _innerGenerator = innerGenerator;
        _budgetService = budgetService;
        _logger = logger;

        // Configure retry policy: 3 retries with exponential backoff
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
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
            // Execute with retry policy
            var audioData = await _retryPolicy.ExecuteAsync(async () =>
                await _innerGenerator.GenerateAudioAsync(text, voiceId, cancellationToken));

            // Record the expense after successful generation
            _budgetService.RecordExpense(estimatedCost);

            return audioData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate audio after retries for text length={Length}", text.Length);
            throw;
        }
    }

    public decimal EstimateCost(string text)
    {
        return _innerGenerator.EstimateCost(text);
    }
}
