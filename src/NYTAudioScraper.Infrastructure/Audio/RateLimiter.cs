// <copyright file="RateLimiter.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Rate limiter using semaphore to control concurrent operations
/// </summary>
public class RateLimiter : IRateLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _minDelayMs;
    private readonly ILogger<RateLimiter> _logger;
    private DateTime _lastReleaseTime = DateTime.MinValue;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new rate limiter
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of concurrent operations</param>
    /// <param name="minDelayMs">Minimum delay between operations in milliseconds</param>
    /// <param name="logger">Logger instance</param>
    public RateLimiter(int maxConcurrency, int minDelayMs, ILogger<RateLimiter> logger)
    {
        if (maxConcurrency <= 0)
            throw new ArgumentException("Max concurrency must be greater than 0", nameof(maxConcurrency));
        if (minDelayMs < 0)
            throw new ArgumentException("Min delay cannot be negative", nameof(minDelayMs));

        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _minDelayMs = minDelayMs;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Acquires the rate limiter, blocking until a slot is available
    /// </summary>
    public async Task AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);

        // Enforce minimum delay between operations
        if (_minDelayMs > 0)
        {
            DateTime now;
            TimeSpan timeSinceLastRelease;

            lock (_lock)
            {
                now = DateTime.UtcNow;
                timeSinceLastRelease = now - _lastReleaseTime;
            }

            if (timeSinceLastRelease.TotalMilliseconds < _minDelayMs)
            {
                var delay = _minDelayMs - (int)timeSinceLastRelease.TotalMilliseconds;
                _logger.LogDebug("Rate limiting: waiting {Delay}ms before next operation", delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Releases the rate limiter, allowing another operation to proceed
    /// </summary>
    public void Release()
    {
        lock (_lock)
        {
            _lastReleaseTime = DateTime.UtcNow;
        }

        _semaphore.Release();
    }

    /// <summary>
    /// Executes an action with rate limiting
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await AcquireAsync(cancellationToken);

        try
        {
            return await action();
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// Gets the current number of available slots
    /// </summary>
    public int AvailableSlots => _semaphore.CurrentCount;

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _semaphore?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
