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
}
