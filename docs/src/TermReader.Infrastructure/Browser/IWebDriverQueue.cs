// Educational and personal use only.

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
/// Serializes access to the shared WebDriver with priority levels.
/// Since WebDriver is single-threaded, all access must be serialized.
/// Foreground requests always preempt background requests.
/// </summary>
public interface IWebDriverQueue
{
    /// <summary>
    /// Gets a value indicating whether a background task currently holds the driver.
    /// Useful for logging and diagnostics.
    /// </summary>
    bool IsBackgroundActive { get; }

    /// <summary>
    /// Acquires the WebDriver for exclusive use. Blocks until available.
    /// Foreground requests are served before background requests.
    /// </summary>
    /// <param name="priority">The priority level of the request.</param>
    /// <param name="headless">Whether the driver should be headless.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease that must be disposed to release the driver.</returns>
    Task<WebDriverLease> AcquireAsync(WebDriverPriority priority, bool headless, CancellationToken cancellationToken);
}

/// <summary>
/// Represents exclusive access to the WebDriver. Dispose to release.
/// </summary>
public sealed class WebDriverLease : IDisposable
{
    private readonly Action _release;
    private bool _disposed;

    internal WebDriverLease(OpenQA.Selenium.IWebDriver driver, Action release)
    {
        Driver = driver;
        _release = release;
    }

    /// <summary>
    /// Gets the WebDriver for use during this lease.
    /// </summary>
    public OpenQA.Selenium.IWebDriver Driver { get; }

    /// <summary>
    /// Releases the WebDriver back to the queue.
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
