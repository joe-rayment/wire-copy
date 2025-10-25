namespace NYTAudioScraper.Infrastructure.Configuration;

/// <summary>
/// Configuration for NYT scraping
/// </summary>
public class NYTConfiguration
{
    public const string SectionName = "NYT";

    public required string Email { get; init; }
    public required string Password { get; init; }
    public string BaseUrl { get; init; } = "https://www.nytimes.com";
    public int MaxArticles { get; init; } = 10;
    public int RateLimitDelayMs { get; init; } = 3000;
}
