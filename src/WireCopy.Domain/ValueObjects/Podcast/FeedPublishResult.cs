// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Podcast;

/// <summary>
/// Result of publishing a podcast feed to cloud storage.
/// </summary>
public record FeedPublishResult
{
    /// <summary>
    /// Gets whether the publish operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the public URL of the published RSS feed.
    /// </summary>
    public required string FeedUrl { get; init; }

    /// <summary>
    /// Gets the number of episodes included in the published feed.
    /// </summary>
    public required int EpisodesPublished { get; init; }

    /// <summary>
    /// Gets the optional error message if the publish failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when the feed was published.
    /// </summary>
    public required DateTime PublishedAtUtc { get; init; }

    /// <summary>
    /// Creates a successful publish result.
    /// </summary>
    public static FeedPublishResult Successful(string feedUrl, int episodesPublished)
    {
        return new FeedPublishResult
        {
            Success = true,
            FeedUrl = feedUrl,
            EpisodesPublished = episodesPublished,
            PublishedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Creates a failed publish result.
    /// </summary>
    public static FeedPublishResult Failure(string errorMessage)
    {
        return new FeedPublishResult
        {
            Success = false,
            FeedUrl = string.Empty,
            EpisodesPublished = 0,
            ErrorMessage = errorMessage,
            PublishedAtUtc = DateTime.UtcNow,
        };
    }
}
