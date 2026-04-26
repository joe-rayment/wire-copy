// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Domain.ValueObjects.Podcast;

/// <summary>
/// Metadata for a podcast feed (channel-level information in RSS).
/// </summary>
public record PodcastMetadata
{
    /// <summary>
    /// Gets the podcast title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the podcast description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the podcast author name.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// Gets the podcast language code (e.g., "en-us").
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the URL to the podcast cover image.
    /// </summary>
    public required string ImageUrl { get; init; }

    /// <summary>
    /// Gets the optional iTunes category (e.g., "News", "Technology").
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Gets whether the podcast contains explicit content.
    /// </summary>
    public bool Explicit { get; init; }

    /// <summary>
    /// Gets the feed URL for atom:link rel=self. Optional; omitted from feed if null.
    /// </summary>
    public string? FeedUrl { get; init; }
}
