// <copyright file="ParallelAudioGenerator.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Parallel audio generator that processes multiple articles concurrently with rate limiting
/// </summary>
public class ParallelAudioGenerator : IParallelAudioGenerator
{
    private readonly IAudioGenerator _audioGenerator;
    private readonly IRateLimiter _rateLimiter;
    private readonly ILogger<ParallelAudioGenerator> _logger;

    /// <summary>
    /// Creates a new parallel audio generator
    /// </summary>
    /// <param name="audioGenerator">The underlying audio generator</param>
    /// <param name="rateLimiter">Rate limiter for controlling concurrency</param>
    /// <param name="logger">Logger instance</param>
    public ParallelAudioGenerator(
        IAudioGenerator audioGenerator,
        IRateLimiter rateLimiter,
        ILogger<ParallelAudioGenerator> logger)
    {
        _audioGenerator = audioGenerator ?? throw new ArgumentNullException(nameof(audioGenerator));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates audio for multiple articles in parallel
    /// </summary>
    /// <param name="articles">Articles to process</param>
    /// <param name="voiceId">Voice ID to use for generation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing successful and failed audio generations</returns>
    public async Task<AudioGenerationResult> GenerateAudioForArticlesAsync(
        IEnumerable<Article> articles,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        var articleList = articles.ToList();
        _logger.LogInformation("Starting parallel audio generation for {Count} articles", articleList.Count);

        var successfulGenerations = new Dictionary<string, byte[]>();
        var failedGenerations = new Dictionary<string, string>();
        var startTime = DateTime.UtcNow;

        // Process articles in parallel with rate limiting
        await Parallel.ForEachAsync(
            articleList,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
            async (article, ct) =>
            {
                try
                {
                    _logger.LogInformation("Generating audio for article: {Title}", article.Title);

                    var audioData = await _rateLimiter.ExecuteAsync(
                        async () => await _audioGenerator.GenerateAudioAsync(article.Content, voiceId, ct),
                        ct);

                    lock (successfulGenerations)
                    {
                        successfulGenerations[article.Id] = audioData;
                    }

                    _logger.LogInformation(
                        "Successfully generated audio for article: {Title} ({Progress}/{Total})",
                        article.Title,
                        successfulGenerations.Count,
                        articleList.Count);
                }
                catch (Exception ex)
                {
                    var errorMessage = $"{ex.GetType().Name}: {ex.Message}";

                    lock (failedGenerations)
                    {
                        failedGenerations[article.Id] = errorMessage;
                    }

                    _logger.LogError(
                        ex,
                        "Failed to generate audio for article: {Title}",
                        article.Title);
                }
            });

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Completed parallel audio generation: {Success} successful, {Failed} failed, {Total} total in {Duration:F1} seconds",
            successfulGenerations.Count,
            failedGenerations.Count,
            articleList.Count,
            duration.TotalSeconds);

        return new AudioGenerationResult
        {
            SuccessfulGenerations = successfulGenerations,
            FailedGenerations = failedGenerations
        };
    }

    /// <summary>
    /// Estimates the total cost for generating audio for multiple articles
    /// </summary>
    public decimal EstimateTotalCost(IEnumerable<Article> articles)
    {
        return articles.Sum(a => _audioGenerator.EstimateCost(a.Content));
    }

    /// <summary>
    /// Estimates the total character count for multiple articles
    /// </summary>
    public int EstimateTotalCharacters(IEnumerable<Article> articles)
    {
        return articles.Sum(a => a.Content.Length);
    }
}
