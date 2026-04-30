// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// Configuration for Anthropic AI integration (link hierarchy analysis).
/// </summary>
public class AnthropicConfiguration
{
    public const string SectionName = "Anthropic";

    /// <summary>
    /// Gets the Anthropic API key. Nullable; checked at runtime, not startup,
    /// so the app can run without AI hierarchy analysis configured.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the model to use for page analysis. Default: claude-haiku-4-5-20251001.
    /// </summary>
    public string Model { get; init; } = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Gets the maximum tokens for the analysis response.
    /// </summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// Gets the maximum budget in USD per analysis request. Prevents runaway costs.
    /// </summary>
    public decimal MaxBudgetUsd { get; init; } = 0.10m;

    /// <summary>
    /// TTL (days) for cached AI Curated results. After this age the analyzer
    /// re-runs on the next visit so layouts adapt to site changes.
    /// </summary>
    public int AiCuratedCacheDays { get; init; } = 30;
}
