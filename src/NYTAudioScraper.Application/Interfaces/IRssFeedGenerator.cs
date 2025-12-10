// <copyright file="IRssFeedGenerator.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Service for generating podcast RSS feeds.
/// </summary>
public interface IRssFeedGenerator
{
    /// <summary>
    /// Generates an RSS feed XML string for multiple individual episodes.
    /// </summary>
    /// <param name="episodes">The podcast episodes to include in the feed.</param>
    /// <param name="podcastInfo">General podcast information.</param>
    /// <returns>The RSS feed as an XML string.</returns>
    string GenerateFeed(IEnumerable<PodcastEpisode> episodes, PodcastInfo podcastInfo);

    /// <summary>
    /// Generates an RSS feed XML string for a single combined episode with chapters.
    /// </summary>
    /// <param name="episode">The combined podcast episode.</param>
    /// <param name="podcastInfo">General podcast information.</param>
    /// <returns>The RSS feed as an XML string.</returns>
    string GenerateCombinedFeed(CombinedPodcastEpisode episode, PodcastInfo podcastInfo);

    /// <summary>
    /// Saves an RSS feed to a file.
    /// </summary>
    /// <param name="episodes">The podcast episodes to include in the feed.</param>
    /// <param name="podcastInfo">General podcast information.</param>
    /// <param name="outputPath">Path to save the feed XML file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveFeedAsync(IEnumerable<PodcastEpisode> episodes, PodcastInfo podcastInfo, string outputPath);

    /// <summary>
    /// Saves an RSS feed for a single combined episode with chapters to a file.
    /// </summary>
    /// <param name="episode">The combined podcast episode.</param>
    /// <param name="podcastInfo">General podcast information.</param>
    /// <param name="outputPath">Path to save the feed XML file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveCombinedFeedAsync(CombinedPodcastEpisode episode, PodcastInfo podcastInfo, string outputPath);
}

/// <summary>
/// Represents a single podcast episode.
/// </summary>
/// <param name="Title">Episode title (e.g., "Headline - Nov 27, 2025").</param>
/// <param name="Description">Episode description or summary.</param>
/// <param name="AudioFileName">Filename of the audio file (for placeholder URLs).</param>
/// <param name="PubDate">Publication date for RSS ordering.</param>
/// <param name="FileSizeBytes">Size of the audio file in bytes.</param>
/// <param name="DurationSeconds">Duration of the episode in seconds.</param>
/// <param name="Guid">Unique identifier for the episode.</param>
public record PodcastEpisode(
    string Title,
    string Description,
    string AudioFileName,
    DateTime PubDate,
    long FileSizeBytes,
    int DurationSeconds,
    string Guid);

/// <summary>
/// General podcast information for the RSS feed.
/// </summary>
/// <param name="Title">Podcast title.</param>
/// <param name="Description">Podcast description.</param>
/// <param name="Author">Podcast author.</param>
/// <param name="Language">Language code (e.g., "en-us").</param>
public record PodcastInfo(
    string Title,
    string Description,
    string Author,
    string Language = "en-us");

/// <summary>
/// Represents a combined podcast episode with chapter information.
/// </summary>
/// <param name="Title">Episode title (e.g., "NYT Today's Paper - Dec 9, 2024").</param>
/// <param name="Description">Episode description listing all articles.</param>
/// <param name="AudioFileName">Filename of the combined audio file.</param>
/// <param name="ChaptersFileName">Filename of the chapters JSON file.</param>
/// <param name="PubDate">Publication date.</param>
/// <param name="FileSizeBytes">Size of the audio file in bytes.</param>
/// <param name="DurationSeconds">Total duration of the episode in seconds.</param>
/// <param name="Guid">Unique identifier for the episode.</param>
/// <param name="ChapterTitles">List of chapter titles for the description.</param>
public record CombinedPodcastEpisode(
    string Title,
    string Description,
    string AudioFileName,
    string ChaptersFileName,
    DateTime PubDate,
    long FileSizeBytes,
    int DurationSeconds,
    string Guid,
    IReadOnlyList<string> ChapterTitles);
