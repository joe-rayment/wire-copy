// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Records why an individual article failed during podcast generation.
/// </summary>
public record ArticleFailure
{
    /// <summary>
    /// Gets the article title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the article URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets a human-readable description of why the article failed.
    /// </summary>
    public required string Reason { get; init; }
}
