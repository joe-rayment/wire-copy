namespace NYTAudioScraper.Infrastructure.Metrics;

/// <summary>
/// Metrics for a single operation
/// </summary>
public class OperationMetrics
{
    public int Count { get; init; }
    public TimeSpan Average { get; init; }
    public TimeSpan Min { get; init; }
    public TimeSpan Max { get; init; }
    public TimeSpan P95 { get; init; }
}
