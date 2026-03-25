// Educational and personal use only.

namespace TermReader.Infrastructure.Configuration;

public class BrowserConfiguration
{
    public const string SectionName = "Browser";

    public string BrowserType { get; init; } = "Chrome"; // "Chrome" or "Firefox" - Primary browser

    public string FallbackBrowserType { get; init; } = "Firefox"; // Fallback browser if primary is blocked

    public bool Headless { get; init; } = true;

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
}
