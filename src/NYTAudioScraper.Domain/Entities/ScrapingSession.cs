namespace NYTAudioScraper.Domain.Entities;

/// <summary>
/// Represents a scraping session with metadata
/// </summary>
public class ScrapingSession
{
    public required string Id { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public required List<Article> Articles { get; init; }
    public string? OutputFilePath { get; set; }
    public int TotalCharactersProcessed { get; set; }
    public decimal EstimatedCost { get; set; }
    public ScrapingStatus Status { get; set; } = ScrapingStatus.InProgress;
    public string? ErrorMessage { get; set; }
}

public enum ScrapingStatus
{
    InProgress,
    Completed,
    Failed,
    PartiallyCompleted
}
