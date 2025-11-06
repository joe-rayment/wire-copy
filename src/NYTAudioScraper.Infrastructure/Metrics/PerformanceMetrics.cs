using System.Collections.Concurrent;
using System.Diagnostics;

namespace NYTAudioScraper.Infrastructure.Metrics;

/// <summary>
/// Tracks performance metrics for operations
/// </summary>
public class PerformanceMetrics
{
    private readonly ConcurrentDictionary<string, List<TimeSpan>> _timings = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();

    /// <summary>
    /// Measures the execution time of an operation
    /// </summary>
    /// <param name="operation">Operation name</param>
    /// <returns>Disposable scope that records timing on disposal</returns>
    public IDisposable Measure(string operation)
    {
        return new MetricScope(this, operation);
    }

    /// <summary>
    /// Records a timing manually
    /// </summary>
    public void RecordTiming(string operation, TimeSpan duration)
    {
        _timings.AddOrUpdate(
            operation,
            _ => new List<TimeSpan> { duration },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(duration);
                }
                return list;
            });
    }

    /// <summary>
    /// Increments a counter
    /// </summary>
    public void Increment(string counter, long value = 1)
    {
        _counters.AddOrUpdate(counter, value, (_, current) => current + value);
    }

    /// <summary>
    /// Gets a summary of all metrics
    /// </summary>
    public MetricsSummary GetSummary()
    {
        var operations = new Dictionary<string, OperationMetrics>();

        foreach (var (operation, timings) in _timings)
        {
            var sorted = timings.OrderBy(t => t.TotalMilliseconds).ToList();

            if (sorted.Count == 0)
                continue;

            var p95Index = (int)(sorted.Count * 0.95);
            if (p95Index >= sorted.Count)
                p95Index = sorted.Count - 1;

            operations[operation] = new OperationMetrics
            {
                Count = sorted.Count,
                Average = TimeSpan.FromMilliseconds(sorted.Average(t => t.TotalMilliseconds)),
                Min = sorted.First(),
                Max = sorted.Last(),
                P95 = sorted[p95Index]
            };
        }

        return new MetricsSummary
        {
            Operations = operations,
            Counters = _counters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }

    private class MetricScope : IDisposable
    {
        private readonly PerformanceMetrics _metrics;
        private readonly string _operation;
        private readonly Stopwatch _stopwatch;

        public MetricScope(PerformanceMetrics metrics, string operation)
        {
            _metrics = metrics;
            _operation = operation;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _metrics.RecordTiming(_operation, _stopwatch.Elapsed);
        }
    }
}

/// <summary>
/// Summary of all performance metrics
/// </summary>
public class MetricsSummary
{
    public Dictionary<string, OperationMetrics> Operations { get; init; } = new();
    public Dictionary<string, long> Counters { get; init; } = new();
}

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
