// Educational and personal use only.

namespace NYTAudioScraper.Application.DTOs.Browser;

/// <summary>
/// Request to load a web page.
/// </summary>
public record PageLoadRequest
{
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
    /// </summary>
    public int TimeoutMs { get; init; } = 30000;
}
