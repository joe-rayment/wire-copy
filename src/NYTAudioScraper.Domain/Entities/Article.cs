namespace NYTAudioScraper.Domain.Entities;

/// <summary>
/// Represents a New York Times article
/// </summary>
public class Article
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Author { get; init; }
    public string? Section { get; init; }
    public required string Content { get; init; }
    public DateTime PublishedDate { get; init; }
    public DateTime ScrapedDate { get; init; }
    public string? AudioFilePath { get; set; }
    public int EstimatedWordCount => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
