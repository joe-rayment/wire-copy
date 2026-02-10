// Educational and personal use only.

namespace TermReader.Infrastructure.Configuration;

/// <summary>
/// Configuration for authentication and scraping.
/// </summary>
public class AuthConfiguration
{
    public const string SectionName = "Auth";

    public string? Email { get; init; }

    public string? Password { get; init; }

    public bool SkipLogin { get; init; } = false;

    public string BaseUrl { get; init; } = "https://www.nytimes.com";

    public int MaxArticles { get; init; } = 10;

    public int RateLimitDelayMs { get; init; } = 3000;
}
