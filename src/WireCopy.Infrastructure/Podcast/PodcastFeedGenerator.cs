// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Generates RSS 2.0 podcast feeds with iTunes, Podcasting 2.0, and Podlove Simple Chapters extensions.
/// Pure function from metadata to XML - no file I/O, no cloud dependencies.
/// </summary>
internal sealed class PodcastFeedGenerator : IPodcastFeedGenerator
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Podcast = "https://podcastindex.org/namespace/1.0";
    private static readonly XNamespace Psc = "http://podlove.org/simple-chapters";

    private readonly ILogger<PodcastFeedGenerator> _logger;

    public PodcastFeedGenerator(ILogger<PodcastFeedGenerator> logger)
    {
        _logger = logger;
    }

    public Task<string> GenerateFeedXmlAsync(
        PodcastMetadata podcast,
        IReadOnlyList<EpisodeMetadata> episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podcast);
        ArgumentNullException.ThrowIfNull(episodes);

        _logger.LogInformation(
            "Generating RSS feed: '{Title}' with {EpisodeCount} episodes",
            podcast.Title,
            episodes.Count);

        var lastBuildDate = episodes.Count > 0
            ? episodes.Max(e => e.PublishedAtUtc)
            : DateTime.UtcNow;

        var channel = BuildChannel(podcast, lastBuildDate);

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            channel.Add(BuildItem(episode));
        }

        var rss = new XElement("rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "atom", Atom),
            new XAttribute(XNamespace.Xmlns + "itunes", Itunes),
            new XAttribute(XNamespace.Xmlns + "podcast", Podcast),
            new XAttribute(XNamespace.Xmlns + "psc", Psc),
            channel);

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            rss);

        using var writer = new Utf8StringWriter();
        document.Save(writer);

        return Task.FromResult(writer.ToString());
    }

    private static XElement BuildChannel(PodcastMetadata podcast, DateTime lastBuildDate)
    {
        var channel = new XElement("channel",
            new XElement("title", podcast.Title),
            new XElement("description", new XCData(podcast.Description)),
            new XElement("language", podcast.Language),
            new XElement("lastBuildDate", lastBuildDate.ToString("R")),
            new XElement("generator", "Wire Copy Podcast Generator"),
            new XElement(Itunes + "author", podcast.Author),
            new XElement(Itunes + "summary", new XCData(podcast.Description)),
            new XElement(Itunes + "explicit", podcast.Explicit ? "true" : "false"),
            new XElement(Itunes + "type", "episodic"),
            new XElement(Podcast + "locked", "yes"));

        if (!string.IsNullOrWhiteSpace(podcast.ImageUrl))
        {
            channel.Add(new XElement(Itunes + "image", new XAttribute("href", podcast.ImageUrl)));
        }

        if (!string.IsNullOrEmpty(podcast.FeedUrl))
        {
            channel.Add(new XElement("link", podcast.FeedUrl));
            channel.Add(new XElement(Atom + "link",
                new XAttribute("href", podcast.FeedUrl),
                new XAttribute("rel", "self"),
                new XAttribute("type", "application/rss+xml")));
        }

        if (!string.IsNullOrEmpty(podcast.Category))
        {
            channel.Add(new XElement(Itunes + "category",
                new XAttribute("text", podcast.Category)));
        }

        return channel;
    }

    private static XElement BuildItem(EpisodeMetadata episode)
    {
        var item = new XElement("item",
            new XElement("title", episode.Title),
            new XElement("description", new XCData(episode.Description)),
            new XElement("pubDate", episode.PublishedAtUtc.ToString("R")),
            new XElement("guid",
                new XAttribute("isPermaLink", "false"),
                episode.Id),
            new XElement("enclosure",
                new XAttribute("url", episode.AudioUrl),
                new XAttribute("length", episode.AudioSizeBytes),
                new XAttribute("type", episode.AudioMimeType)),
            new XElement(Itunes + "duration", FormatDuration(episode.Duration)),
            new XElement(Itunes + "summary", new XCData(episode.Description)));

        // Podlove Simple Chapters
        if (episode.Chapters is { Count: > 0 })
        {
            var chaptersElement = new XElement(Psc + "chapters",
                new XAttribute("version", "1.2"));

            foreach (var chapter in episode.Chapters)
            {
                var chapterElement = new XElement(Psc + "chapter",
                    new XAttribute("start", FormatChapterTime(chapter.StartTime)),
                    new XAttribute("title", chapter.Title));

                if (!string.IsNullOrEmpty(chapter.LinkUrl))
                {
                    chapterElement.Add(new XAttribute("href", chapter.LinkUrl));
                }

                if (!string.IsNullOrEmpty(chapter.ImageUrl))
                {
                    chapterElement.Add(new XAttribute("image", chapter.ImageUrl));
                }

                chaptersElement.Add(chapterElement);
            }

            item.Add(chaptersElement);
        }

        return item;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        // iTunes requires HH:MM:SS format
        return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    private static string FormatChapterTime(TimeSpan time)
    {
        // Podlove format: HH:MM:SS.mmm
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
