// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

public class BrowserConfiguration
{
    public const string SectionName = "Browser";

    public string BrowserType { get; init; } = "Chrome"; // "Chrome" or "Firefox" - Primary browser

    public string FallbackBrowserType { get; init; } = "Firefox"; // Fallback browser if primary is blocked

    public bool Headless { get; init; } = true;

    /// <summary>
    /// When true, disables all UI animations. The timer-tick mechanism in the input handler
    /// will not fire, and animation frames will be skipped. Useful for low-resource environments
    /// or terminals that don't handle rapid redraws well.
    /// </summary>
    public bool DisableAnimations { get; init; }

    /// <summary>
    /// Which side of the screen the headed browser docks to when toggled into the
    /// side-by-side "concert" view. Right (default) keeps the terminal on the left.
    /// </summary>
    public DockSide DockSide { get; init; } = DockSide.Right;

    /// <summary>
    /// Fraction of the screen width the docked browser window occupies (the terminal
    /// gets the rest). Default 0.5 (an even split). Clamped to [0.2, 0.8] at use.
    /// </summary>
    public double DockFraction { get; init; } = 0.5;

    /// <summary>
    /// Gets implicit wait timeout in seconds. Pages with heavy JavaScript and dynamic content
    /// require longer waits. Recommended: 30+ seconds for reliable element detection.
    /// </summary>
    public int ImplicitWaitSeconds { get; init; } = 10;

    /// <summary>
    /// Gets page load timeout in seconds. Some pages can be very slow to fully load, especially with
    /// images disabled and extensive JavaScript processing. A generous timeout
    /// prevents false timeouts while waiting for document.readyState to complete.
    /// </summary>
    public int PageLoadTimeoutSeconds { get; init; } = 30;

    public bool DisableImages { get; init; } = true;

    public bool DisableJavaScript { get; init; } = false;

    public string UserAgent { get; init; } = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Gets optional delay (ms) after document.readyState is complete.
    /// Use for sites that need extra time for JS rendering. Default: 0 (no delay).
    /// </summary>
    public int PostLoadDelayMs { get; init; } = 500;

    /// <summary>
    /// Gets HTTP fetch timeout (ms) before falling back to browser.
    /// A short timeout ensures fast fallback to browser for JS-heavy sites.
    /// </summary>
    public int HttpTimeoutMs { get; init; } = 3000;

    /// <summary>
    /// Gets the interval (ms) between bot challenge page polls.
    /// After detecting a bot challenge, the browser re-checks the page source at this interval.
    /// </summary>
    public int BotChallengePollIntervalMs { get; init; } = 3000;

    /// <summary>
    /// Gets the maximum wait time (ms) for a bot challenge to auto-resolve.
    /// If the challenge persists beyond this duration, the page load is reported as a failure.
    /// </summary>
    public int BotChallengeMaxWaitMs { get; init; } = 60000;

    /// <summary>
    /// Domains known to require authenticated browser sessions (paywall).
    /// When cookies exist for these domains, skip HTTP and use the browser directly.
    /// </summary>
    public string[] PaywalledDomains { get; init; } = ["nytimes.com", "wsj.com", "washingtonpost.com", "theathletic.com"];

    public string[] ExperimentalOptions { get; init; } =
    [
        "excludeSwitches", "enable-automation",
        "useAutomationExtension", "false"
    ];

    /// <summary>
    /// Checks whether the given URL belongs to a known paywalled domain.
    /// Matches both exact domain (e.g., "nytimes.com") and subdomains (e.g., "www.nytimes.com").
    /// </summary>
    public bool IsPaywalledDomain(string url)
    {
        if (PaywalledDomains.Length == 0)
        {
            return false;
        }

        try
        {
            var host = new Uri(url).Host;
            return PaywalledDomains.Any(d =>
                host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
