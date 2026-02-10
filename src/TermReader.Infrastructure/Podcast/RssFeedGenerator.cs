// <copyright file="RssFeedGenerator.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Generates podcast RSS 2.0 feeds compatible with Apple Podcasts.
/// </summary>
public class RssFeedGenerator : IRssFeedGenerator
{
    private const string ItunesNamespace = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private const string PodcastNamespace = "https://podcastindex.org/namespace/1.0";
    private const string PlaceholderBaseUrl = "{{BASE_URL}}";

    private readonly ILogger<RssFeedGenerator> _logger;

    public RssFeedGenerator(ILogger<RssFeedGenerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates podcast episodes with correct pubDate ordering.
    /// First article in the list gets the newest pubDate so it appears first in podcast apps.
    /// </summary>
    /// <param name="articles">Articles in display order (first = should play first).</param>
    /// <param name="baseTime">Base time for the first article's pubDate.</param>
    /// <returns>Episodes with correctly offset pubDates.</returns>
    public static IEnumerable<PodcastEpisode> CreateOrderedEpisodes(
        IEnumerable<(string Title, string Description, string FileName, long FileSize, int DurationSecs, string Guid, DateTime ArticleDate)> articles,
        DateTime? baseTime = null)
    {
        var baseDateTime = baseTime ?? DateTime.UtcNow;
        var articleList = articles.ToList();
        var episodes = new List<PodcastEpisode>();

        for (int i = 0; i < articleList.Count; i++)
        {
            var article = articleList[i];

            // First article gets baseTime, each subsequent article is 1 second earlier
            // This ensures correct ordering in RSS (newest first = first to play)
            var pubDate = baseDateTime.AddSeconds(-i);

            episodes.Add(new PodcastEpisode(
                Title: article.Title,
                Description: article.Description,
                AudioFileName: article.FileName,
                PubDate: pubDate,
                FileSizeBytes: article.FileSize,
                DurationSeconds: article.DurationSecs,
                Guid: article.Guid));
        }

        return episodes;
    }

    /// <inheritdoc/>
    public string GenerateFeed(IEnumerable<PodcastEpisode> episodes, PodcastInfo podcastInfo)
    {
        var episodeList = episodes.ToList();

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);

        writer.WriteStartDocument();

        // RSS root element with iTunes namespace
        writer.WriteStartElement("rss");
        writer.WriteAttributeString("version", "2.0");
        writer.WriteAttributeString("xmlns", "itunes", null, ItunesNamespace);

        // Channel element
        writer.WriteStartElement("channel");

        // Podcast metadata
        writer.WriteElementString("title", podcastInfo.Title);
        writer.WriteElementString("description", podcastInfo.Description);
        writer.WriteElementString("language", podcastInfo.Language);
        writer.WriteElementString("generator", "TermReader");

        // iTunes-specific elements
        writer.WriteStartElement("itunes", "author", ItunesNamespace);
        writer.WriteString(podcastInfo.Author);
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "explicit", ItunesNamespace);
        writer.WriteString("no");
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "category", ItunesNamespace);
        writer.WriteAttributeString("text", "News");
        writer.WriteEndElement();

        // Write episodes
        // Episodes are already ordered by PubDate descending (first article = newest pubDate)
        foreach (var episode in episodeList)
        {
            WriteEpisode(writer, episode);
        }

        writer.WriteEndElement(); // channel
        writer.WriteEndElement(); // rss

        writer.WriteEndDocument();
        writer.Flush();

        var feed = stringWriter.ToString();
        _logger.LogInformation("Generated RSS feed with {Count} episodes", episodeList.Count);

        return feed;
    }

    /// <inheritdoc/>
    public async Task SaveFeedAsync(IEnumerable<PodcastEpisode> episodes, PodcastInfo podcastInfo, string outputPath)
    {
        var feed = GenerateFeed(episodes, podcastInfo);
        await File.WriteAllTextAsync(outputPath, feed, Encoding.UTF8);
        _logger.LogInformation("Saved RSS feed to: {Path}", outputPath);
    }

    /// <inheritdoc/>
    public string GenerateCombinedFeed(CombinedPodcastEpisode episode, PodcastInfo podcastInfo)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);

        writer.WriteStartDocument();

        // RSS root element with iTunes and Podcasting 2.0 namespaces
        writer.WriteStartElement("rss");
        writer.WriteAttributeString("version", "2.0");
        writer.WriteAttributeString("xmlns", "itunes", null, ItunesNamespace);
        writer.WriteAttributeString("xmlns", "podcast", null, PodcastNamespace);

        // Channel element
        writer.WriteStartElement("channel");

        // Podcast metadata
        writer.WriteElementString("title", podcastInfo.Title);
        writer.WriteElementString("description", podcastInfo.Description);
        writer.WriteElementString("language", podcastInfo.Language);
        writer.WriteElementString("generator", "TermReader");

        // iTunes-specific elements
        writer.WriteStartElement("itunes", "author", ItunesNamespace);
        writer.WriteString(podcastInfo.Author);
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "explicit", ItunesNamespace);
        writer.WriteString("no");
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "category", ItunesNamespace);
        writer.WriteAttributeString("text", "News");
        writer.WriteEndElement();

        // Podcasting 2.0 locked tag
        writer.WriteStartElement("podcast", "locked", PodcastNamespace);
        writer.WriteString("no");
        writer.WriteEndElement();

        // Write the single combined episode
        WriteCombinedEpisode(writer, episode);

        writer.WriteEndElement(); // channel
        writer.WriteEndElement(); // rss

        writer.WriteEndDocument();
        writer.Flush();

        var feed = stringWriter.ToString();
        _logger.LogInformation(
            "Generated combined RSS feed with {ChapterCount} chapters",
            episode.ChapterTitles.Count);

        return feed;
    }

    /// <inheritdoc/>
    public async Task SaveCombinedFeedAsync(CombinedPodcastEpisode episode, PodcastInfo podcastInfo, string outputPath)
    {
        var feed = GenerateCombinedFeed(episode, podcastInfo);
        await File.WriteAllTextAsync(outputPath, feed, Encoding.UTF8);
        _logger.LogInformation("Saved combined RSS feed to: {Path}", outputPath);
    }

    private static void WriteCombinedEpisode(XmlWriter writer, CombinedPodcastEpisode episode)
    {
        writer.WriteStartElement("item");

        // Standard RSS elements
        writer.WriteElementString("title", episode.Title);

        // Build description that lists all articles/chapters
        var descriptionBuilder = new StringBuilder();
        descriptionBuilder.AppendLine(episode.Description);
        descriptionBuilder.AppendLine();
        descriptionBuilder.AppendLine("Articles in this episode:");
        for (int i = 0; i < episode.ChapterTitles.Count; i++)
        {
            descriptionBuilder.AppendLine($"{i + 1}. {episode.ChapterTitles[i]}");
        }

        writer.WriteElementString("description", descriptionBuilder.ToString());
        writer.WriteElementString("guid", episode.Guid);

        // RFC 822 date format for RSS
        writer.WriteElementString(
            "pubDate",
            episode.PubDate.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT");

        // Enclosure (the combined M4A audio file)
        writer.WriteStartElement("enclosure");
        writer.WriteAttributeString("url", $"{PlaceholderBaseUrl}/{episode.AudioFileName}");
        writer.WriteAttributeString("length", episode.FileSizeBytes.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("type", "audio/mp4"); // M4A is audio/mp4
        writer.WriteEndElement();

        // iTunes duration (HH:MM:SS format)
        var duration = TimeSpan.FromSeconds(episode.DurationSeconds);
        writer.WriteStartElement("itunes", "duration", ItunesNamespace);
        writer.WriteString(duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        writer.WriteEndElement();

        // Podcasting 2.0 chapters tag
        writer.WriteStartElement("podcast", "chapters", PodcastNamespace);
        writer.WriteAttributeString("url", $"{PlaceholderBaseUrl}/{episode.ChaptersFileName}");
        writer.WriteAttributeString("type", "application/json+chapters");
        writer.WriteEndElement();

        writer.WriteEndElement(); // item
    }

    private static void WriteEpisode(XmlWriter writer, PodcastEpisode episode)
    {
        writer.WriteStartElement("item");

        // Standard RSS elements
        writer.WriteElementString("title", episode.Title);
        writer.WriteElementString("description", episode.Description);
        writer.WriteElementString("guid", episode.Guid);

        // RFC 822 date format for RSS
        writer.WriteElementString(
            "pubDate",
            episode.PubDate.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT");

        // Enclosure (the actual audio file)
        writer.WriteStartElement("enclosure");
        writer.WriteAttributeString("url", $"{PlaceholderBaseUrl}/{episode.AudioFileName}");
        writer.WriteAttributeString("length", episode.FileSizeBytes.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("type", "audio/mpeg");
        writer.WriteEndElement();

        // iTunes duration (HH:MM:SS format)
        var duration = TimeSpan.FromSeconds(episode.DurationSeconds);
        writer.WriteStartElement("itunes", "duration", ItunesNamespace);
        writer.WriteString(duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        writer.WriteEndElement();

        writer.WriteEndElement(); // item
    }
}
