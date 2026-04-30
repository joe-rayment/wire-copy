// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// A scraping strategy decides WHICH links the user sees on a link-list page.
/// Three strategies ship today:
/// <list type="bullet">
///   <item>Document Order (always available)</item>
///   <item>AI Curated (requires Anthropic API key)</item>
///   <item>RSS Feed (requires a discoverable feed)</item>
/// </list>
/// Visual cell rendering is fixed; strategies do NOT change cell style.
/// </summary>
public interface IScrapingStrategy
{
    /// <summary>
    /// Stable identifier persisted in the per-domain config
    /// ("DocumentOrder", "AiCurated", "RssFeed").
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable label shown in the chooser.</summary>
    string DisplayName { get; }

    /// <summary>
    /// One-line description shown in the chooser
    /// (e.g. "AI-curated · removes ads, ranks stories").
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Decides whether the strategy is available for this page right now.
    /// Implementations may probe (RSS feed detection) or check config (API key).
    /// The result is also used to surface a helpful "unavailable because…" message.
    /// </summary>
    /// <param name="context">Page-load context required for the decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Availability result with optional reason text.</returns>
    Task<ScrapingStrategyAvailability> IsAvailableAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the navigation tree for the page using this strategy.
    /// May call external services (AI, RSS feed) the first time;
    /// subsequent calls should use the cached payload from
    /// <paramref name="context"/>.SavedConfig when present.
    /// </summary>
    /// <returns>The built navigation tree and the (potentially updated) config to persist.</returns>
    Task<ScrapingStrategyResult> BuildTreeAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Inputs required by every scraping strategy.
/// </summary>
public sealed record ScrapingStrategyContext
{
    /// <summary>Original / final URL of the page being scraped.</summary>
    public required string PageUrl { get; init; }

    /// <summary>Raw HTML of the page (used for RSS link detection).</summary>
    public required string Html { get; init; }

    /// <summary>Links already extracted by <see cref="ILinkExtractor"/>.</summary>
    public required IReadOnlyList<LinkInfo> Links { get; init; }

    /// <summary>Optional screenshot bytes for AI-based strategies.</summary>
    public byte[]? Screenshot { get; init; }

    /// <summary>
    /// Existing saved config for the domain (if any). Strategies may use
    /// the cached AI result or RSS URL stored on this record.
    /// </summary>
    public SiteHierarchyConfig? SavedConfig { get; init; }
}

/// <summary>
/// Result of the availability check.
/// </summary>
public sealed record ScrapingStrategyAvailability
{
    /// <summary>True if the strategy can run for this page.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// When unavailable, a short user-facing reason
    /// ("No Anthropic API key", "No RSS feed found").
    /// </summary>
    public string? ReasonWhenUnavailable { get; init; }

    /// <summary>
    /// Optional descriptor surfaced in the chooser
    /// (e.g. RSS feed URL with article count).
    /// </summary>
    public string? StatusDetail { get; init; }
}

/// <summary>
/// Strategy build output.
/// </summary>
public sealed record ScrapingStrategyResult
{
    /// <summary>The navigation tree to render.</summary>
    public required NavigationTree Tree { get; init; }

    /// <summary>
    /// The config to persist (caller decides when to save). Includes any
    /// cached AI result or chosen RSS feed URL.
    /// </summary>
    public required SiteHierarchyConfig Config { get; init; }

    /// <summary>Human-readable summary for status messages.</summary>
    public string? Summary { get; init; }
}
