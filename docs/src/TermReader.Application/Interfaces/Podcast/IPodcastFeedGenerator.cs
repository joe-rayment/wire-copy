// Educational and personal use only.

using TermReader.Domain.ValueObjects.Podcast;

namespace TermReader.Application.Interfaces.Podcast;

/// <summary>
/// Generates podcast RSS feed XML from metadata and episode information.
/// </summary>
public interface IPodcastFeedGenerator
{
    /// <summary>
    /// Generates an RSS 2.0 podcast feed XML string with iTunes extensions.
    /// </summary>
    /// <param name="podcast">The podcast channel metadata.</param>
    /// <param name="episodes">The episodes to include in the feed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated RSS XML as a string.</returns>
    Task<string> GenerateFeedXmlAsync(
        PodcastMetadata podcast,
        IReadOnlyList<EpisodeMetadata> episodes,
        CancellationToken cancellationToken = default);
}
