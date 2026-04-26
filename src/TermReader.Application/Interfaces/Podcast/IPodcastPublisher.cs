// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.DTOs.Podcast;
using TermReader.Domain.ValueObjects.Podcast;

namespace TermReader.Application.Interfaces.Podcast;

/// <summary>
/// Orchestrates publishing podcast episodes: uploads audio, generates feed, and publishes to cloud storage.
/// </summary>
public interface IPodcastPublisher
{
    /// <summary>
    /// Publishes new episodes to the podcast feed, uploading audio files and updating the RSS feed.
    /// </summary>
    /// <param name="podcast">The podcast channel metadata.</param>
    /// <param name="episodes">The episode sources to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The publish result with the feed URL and episode count, or an error.</returns>
    Task<FeedPublishResult> PublishFeedAsync(
        PodcastMetadata podcast,
        IReadOnlyList<EpisodeSource> episodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the existing feed URL for a podcast by title, or null if no feed has been published.
    /// </summary>
    /// <param name="title">The podcast title to search for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The feed URL if found, otherwise null.</returns>
    Task<string?> GetExistingFeedUrlAsync(
        string title,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an empty but valid RSS feed for a new bucket, so the user gets a subscribable feed URL immediately.
    /// Returns the existing feed URL if one already exists.
    /// </summary>
    /// <param name="podcast">The podcast channel metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The publish result with the feed URL.</returns>
    Task<FeedPublishResult> BootstrapFeedAsync(
        PodcastMetadata podcast,
        CancellationToken cancellationToken = default);
}
