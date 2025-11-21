// Educational and personal use only.

namespace NYTAudioScraper.Infrastructure.Browser.Models;

/// <summary>
/// Represents an individual cookie.
/// </summary>
public class CookieData
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public required string Domain { get; init; }

    public required string Path { get; init; }

    public DateTime? Expiry { get; init; }
}
