// <copyright file="AdaptiveRateLimiter.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using Microsoft.Extensions.Logging;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Adaptive rate limiter that adjusts delays based on API response headers
/// </summary>
public class AdaptiveRateLimiter
{
    private readonly ILogger<AdaptiveRateLimiter> _logger;
    private int _currentDelayMs;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly object _lock = new();

    public AdaptiveRateLimiter(
        int minDelayMs = 1000,
        int maxDelayMs = 10000,
        ILogger<AdaptiveRateLimiter>? logger = null)
    {
        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _currentDelayMs = minDelayMs;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Waits for the current delay period
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        int delay;
        lock (_lock)
        {
            delay = _currentDelayMs;
        }

        _logger.LogDebug("Adaptive rate limiter: waiting {Delay}ms", delay);
        await Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Updates the delay based on Retry-After header
    /// </summary>
    /// <param name="retryAfterSeconds">Retry-After header value in seconds</param>
    public void UpdateDelayFromRetryAfter(int retryAfterSeconds)
    {
        var newDelayMs = retryAfterSeconds * 1000;

        lock (_lock)
        {
            _currentDelayMs = Math.Clamp(newDelayMs, _minDelayMs, _maxDelayMs);
        }

        _logger.LogInformation(
            "Rate limit updated from Retry-After header: {Delay}ms",
            _currentDelayMs);
    }

    /// <summary>
    /// Increases the delay when rate limited (exponential backoff)
    /// </summary>
    public void IncreaseDelay()
    {
        lock (_lock)
        {
            _currentDelayMs = Math.Min(_currentDelayMs * 2, _maxDelayMs);
        }

        _logger.LogWarning("Rate limit hit - increasing delay to {Delay}ms", _currentDelayMs);
    }

    /// <summary>
    /// Decreases the delay when successful (gradual recovery)
    /// </summary>
    public void DecreaseDelay()
    {
        lock (_lock)
        {
            _currentDelayMs = Math.Max(_currentDelayMs - 100, _minDelayMs);
        }

        _logger.LogDebug("Successful request - decreasing delay to {Delay}ms", _currentDelayMs);
    }

    /// <summary>
    /// Resets the delay to the minimum
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _currentDelayMs = _minDelayMs;
        }

        _logger.LogInformation("Rate limiter reset to minimum delay: {Delay}ms", _minDelayMs);
    }

    /// <summary>
    /// Gets the current delay in milliseconds
    /// </summary>
    public int CurrentDelayMs
    {
        get
        {
            lock (_lock)
            {
                return _currentDelayMs;
            }
        }
    }
}
