// Educational and personal use only.

using OpenQA.Selenium;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Manages the lifecycle of a shared WebDriver instance,
/// allowing driver reuse across page loads within a session.
/// </summary>
public interface IBrowserSession : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether there is an active WebDriver instance.
    /// </summary>
    bool HasActiveDriver { get; }

    /// <summary>
    /// Gets or creates a WebDriver instance. Returns the existing driver
    /// if one is active, or creates a new one if none exists or the previous
    /// driver has crashed.
    /// </summary>
    /// <param name="headless">Whether to run the browser in headless mode.</param>
    /// <returns>An active WebDriver instance.</returns>
    IWebDriver GetOrCreateDriver(bool headless);

    /// <summary>
    /// Releases the current driver reference without disposing it,
    /// allowing it to be reused by subsequent calls.
    /// </summary>
    void ReleaseDriver();

    /// <summary>
    /// Eagerly initializes the browser driver in the background so the first
    /// browser-based page load does not incur the cold-start penalty.
    /// Safe to call concurrently with GetOrCreateDriver (guarded by internal lock).
    /// </summary>
    Task WarmUpAsync();
}
