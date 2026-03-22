// Educational and personal use only.

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Request to load a web page.
/// </summary>
public record PageLoadRequest
{
    private readonly int _timeoutMs = 30000;

    /// <summary>
    /// URL to load.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Whether to use headless browser mode.
    /// </summary>
    public bool Headless { get; init; } = true;

    /// <summary>
    /// Maximum time to wait for page load (milliseconds).
    /// Clamped to [1000, 300000].
    /// </summary>
    public int TimeoutMs
    {
        get => _timeoutMs;
        init => _timeoutMs = Math.Clamp(value, 1000, 300000);
    }

    /// <summary>
    /// Whether to bypass the cache and force a fresh network fetch.
    /// </summary>
    public bool ForceRefresh { get; init; }

    /// <summary>
    /// Whether to skip the HTTP fetch and go straight to Selenium.
    /// Used for known-paywalled domains where cookies are needed for authentication.
    /// </summary>
    public bool ForceBrowser { get; init; }

    /// <summary>
    /// Whether to prefer Selenium over HTTP for the initial fetch attempt.
    /// When true, tries Selenium first and falls back to HTTP on failure.
    /// When false (default), tries HTTP first and falls back to Selenium.
    /// Unlike <see cref="ForceBrowser"/>, this still allows HTTP as a fallback.
    /// </summary>
    public bool PreferSelenium { get; init; }
}
