// Educational and personal use only.

using OpenQA.Selenium;
using TermReader.Application.Interfaces.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Infrastructure-level interface extending <see cref="IBrowserSessionControl"/>
/// with Selenium-specific driver access for browser automation components.
/// </summary>
public interface IBrowserSession : IBrowserSessionControl
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
}
