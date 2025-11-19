// <copyright file="AudioChapter.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


namespace NYTAudioScraper.Domain.Entities;

/// <summary>
/// Represents a chapter in the audiobook
/// </summary>
public class AudioChapter
{
    public required string Title { get; init; }
    public required string ArticleId { get; init; }
    public required int StartTimeMs { get; init; }
    public required int DurationMs { get; init; }
    public int EndTimeMs => StartTimeMs + DurationMs;
    public string? AudioFilePath { get; set; }
}
