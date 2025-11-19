namespace NYTAudioScraper.Infrastructure.Metrics;

/// <summary>
/// Summary of all performance metrics
/// </summary>
public class MetricsSummary
{
    public Dictionary<string, OperationMetrics> Operations { get; init; } = new();
    public Dictionary<string, long> Counters { get; init; } = new();
}
