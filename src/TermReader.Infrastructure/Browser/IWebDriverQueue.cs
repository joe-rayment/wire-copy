// Educational and personal use only.

using Microsoft.Playwright;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Priority levels for WebDriver access.
/// Foreground requests (user navigation) preempt background requests (article scraping).
/// </summary>
public enum WebDriverPriority
{
    /// <summary>
    /// User-initiated navigation. Gets immediate priority over background tasks.
    /// </summary>
    Foreground,

    /// <summary>
    /// Background article scraping. Yields to foreground requests.
    /// </summary>
    Background,
}

/// <summary>
/// Serializes access to the shared browser page with priority levels.
/// Since the browser context is single-threaded, all access must be serialized.
/// Foreground requests always preempt background requests.
/// </summary>
public interface IWebDriverQueue
{
    /// <summary>
    /// Gets a value indicating whether a background task currently holds the page.
    /// Useful for logging and diagnostics.
    /// </summary>
    bool IsBackgroundActive { get; }

    /// <summary>
    /// Acquires the browser page for exclusive use. Blocks until available.
    /// Foreground requests are served before background requests.
    /// </summary>
    /// <param name="priority">The priority level of the request.</param>
    /// <param name="headless">Whether the browser should be headless.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease that must be disposed to release the page.</returns>
    Task<WebDriverLease> AcquireAsync(WebDriverPriority priority, bool headless, CancellationToken cancellationToken);
}

/// <summary>
/// Represents exclusive access to the browser page. Dispose to release.
/// </summary>
public sealed class WebDriverLease : IDisposable
{
    private readonly Action _release;
    private bool _disposed;

    internal WebDriverLease(IPage page, Action release)
    {
        Page = page;
        _release = release;
    }

    /// <summary>
    /// Gets the Playwright page for use during this lease.
    /// </summary>
    public IPage Page { get; }

    /// <summary>
    /// Releases the browser page back to the queue.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _release();
    }
}
