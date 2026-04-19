// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Result of <see cref="PageLoadPipeline.LoadAsync"/> carrying
/// the assembled page, the fetch method used, and the build cache snapshot.
/// </summary>
public sealed record PageLoadPipelineResult
{
    /// <summary>
    /// The assembled page from the load pipeline.
    /// </summary>
    public required Page Page { get; init; }

    /// <summary>
    /// How the page was fetched (HTTP, browser, or cached).
    /// </summary>
    public required FetchMethod FetchMethod { get; init; }

    /// <summary>
    /// Build cache snapshot captured during <see cref="PageLoadPipeline.BuildPageAsync"/>.
    /// Null when the page was served from article content cache or build cache.
    /// </summary>
    public PageBuildCache? BuildResult { get; init; }

    /// <summary>
    /// Background task that attempts to improve the page quality (e.g., paywall retry,
    /// bot challenge retry). Null when no quality improvement is needed.
    /// The caller should await this and replace the current page if it produces a better result.
    /// </summary>
    public Task<PageLoadPipelineResult>? QualityRetryTask { get; init; }
}
