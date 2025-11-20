// <copyright file="NYTConfiguration.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

namespace NYTAudioScraper.Infrastructure.Configuration;

/// <summary>
/// Configuration for NYT scraping
/// </summary>
public class NYTConfiguration
{
    public const string SectionName = "NYT";

    public string? Email { get; init; }
    public string? Password { get; init; }
    public bool SkipLogin { get; init; } = false;
    public string BaseUrl { get; init; } = "https://www.nytimes.com";
    public int MaxArticles { get; init; } = 10;
    public int RateLimitDelayMs { get; init; } = 3000;
}
