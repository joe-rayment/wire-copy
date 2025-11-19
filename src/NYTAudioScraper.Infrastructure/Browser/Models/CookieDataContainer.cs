// <copyright file="CookieDataContainer.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


namespace NYTAudioScraper.Infrastructure.Browser.Models;

/// <summary>
/// Cookie data wrapper with metadata
/// </summary>
public class CookieDataContainer
{
    /// <summary>
    /// List of cookies
    /// </summary>
    public required List<CookieData> Cookies { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
