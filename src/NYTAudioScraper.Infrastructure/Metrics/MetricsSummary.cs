// <copyright file="MetricsSummary.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

namespace NYTAudioScraper.Infrastructure.Metrics;

/// <summary>
/// Summary of all performance metrics
/// </summary>
public class MetricsSummary
{
    public Dictionary<string, OperationMetrics> Operations { get; init; } = new();
    public Dictionary<string, long> Counters { get; init; } = new();
}
