using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Parallel audio generator that processes multiple articles concurrently with rate limiting
/// </summary>
public class ParallelAudioGenerator
{
    private readonly IAudioGenerator _audioGenerator;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<ParallelAudioGenerator> _logger;

    /// <summary>
    /// Creates a new parallel audio generator
    /// </summary>
    /// <param name="audioGenerator">The underlying audio generator</param>
    /// <param name="rateLimiter">Rate limiter for controlling concurrency</param>
    /// <param name="logger">Logger instance</param>
    public ParallelAudioGenerator(
        IAudioGenerator audioGenerator,
        RateLimiter rateLimiter,
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
    /// <returns>Dictionary mapping article IDs to audio data</returns>
    public async Task<Dictionary<string, byte[]>> GenerateAudioForArticlesAsync(
        IEnumerable<Article> articles,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        var articleList = articles.ToList();
        _logger.LogInformation("Starting parallel audio generation for {Count} articles", articleList.Count);

        var results = new Dictionary<string, byte[]>();
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

                    lock (results)
                    {
                        results[article.Id] = audioData;
                    }

                    _logger.LogInformation(
                        "Successfully generated audio for article: {Title} ({Progress}/{Total})",
                        article.Title,
                        results.Count,
                        articleList.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to generate audio for article: {Title}",
                        article.Title);

                    // Continue processing other articles even if one fails
                }
            });

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Completed parallel audio generation: {Success}/{Total} articles in {Duration:F1} seconds",
            results.Count,
            articleList.Count,
            duration.TotalSeconds);

        return results;
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
