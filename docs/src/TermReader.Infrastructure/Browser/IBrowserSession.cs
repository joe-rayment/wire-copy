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
    /// Gets a value indicating whether Selenium WebDriver can be used on this platform.
    /// Returns false on ARM64 Linux where Selenium Manager downloads an incompatible
    /// x86_64 chromedriver binary.
    /// </summary>
    bool IsSeleniumAvailable { get; }

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
    /// Restores a minimized browser window to normal size for interactive use.
    /// No-op if headless or no active driver.
    /// </summary>
    void RestoreWindow();

    /// <summary>
    /// Captures a viewport screenshot of the current page as PNG bytes.
    /// Returns null if no active driver or capture fails.
    /// </summary>
    byte[]? CaptureScreenshot();
}
