// <copyright file="IRateLimiter.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Rate limiter for controlling concurrent operations and enforcing minimum delays
/// </summary>
public interface IRateLimiter : IDisposable
{
    /// <summary>
    /// Acquires the rate limiter, blocking until a slot is available
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the rate limiter, allowing another operation to proceed
    /// </summary>
    void Release();

    /// <summary>
    /// Executes an action with rate limiting
    /// </summary>
    /// <typeparam name="T">The return type of the action</typeparam>
    /// <param name="action">The action to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the action</returns>
    Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current number of available slots
    /// </summary>
    int AvailableSlots { get; }
}
