// Licensed under the MIT License. See LICENSE in the repository root.

using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class PodcastFeedGeneratorTests
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace PodcastNs = "https://podcastindex.org/namespace/1.0";
    private static readonly XNamespace Psc = "http://podlove.org/simple-chapters";

    private readonly PodcastFeedGenerator _generator = new(NullLogger<PodcastFeedGenerator>.Instance);

    private static PodcastMetadata CreateTestPodcast() => new()
    {
        Title = "Test Podcast",
        Description = "A test podcast description",
        Author = "Test Author",
        Language = "en-us",
        ImageUrl = "https://example.com/cover.jpg",
        Category = "Technology",
        Explicit = false,
    };

    private static EpisodeMetadata CreateTestEpisode(string id = "ep1") => new()
    {
        Id = id,
        Title = "Episode 1",
        Description = "Episode description",
        PublishedAtUtc = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        AudioUrl = "https://storage.example.com/ep1.m4a",
        AudioSizeBytes = 1024000,
        Duration = TimeSpan.FromMinutes(15),
        AudioMimeType = "audio/x-m4a",
    };

    [Fact]
    public async Task GenerateFeedXmlAsync_ValidXmlStructure()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeMetadata> { CreateTestEpisode() };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);

        var doc = XDocument.Parse(xml);
        doc.Root!.Name.LocalName.Should().Be("rss");
        doc.Root.Attribute("version")!.Value.Should().Be("2.0");
        doc.Root.Element("channel").Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_ContainsRequiredNamespaces()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeMetadata> { CreateTestEpisode() };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);

        xml.Should().Contain("itunes.com/dtds/podcast-1.0.dtd");
        xml.Should().Contain("podcastindex.org/namespace/1.0");
        xml.Should().Contain("podlove.org/simple-chapters");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_ItunesTagsPresent()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeMetadata> { CreateTestEpisode() };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);
        var doc = XDocument.Parse(xml);
        var channel = doc.Root!.Element("channel")!;

        channel.Element(Itunes + "author")!.Value.Should().Be("Test Author");
        channel.Element(Itunes + "summary").Should().NotBeNull();
        channel.Element(Itunes + "image")!.Attribute("href")!.Value.Should().Be("https://example.com/cover.jpg");
        channel.Element(Itunes + "explicit")!.Value.Should().Be("no");
        channel.Element(Itunes + "type")!.Value.Should().Be("episodic");
        channel.Element(Itunes + "category")!.Attribute("text")!.Value.Should().Be("Technology");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_PodcastLockedTag()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeMetadata> { CreateTestEpisode() };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);
        var doc = XDocument.Parse(xml);
        var channel = doc.Root!.Element("channel")!;

        channel.Element(PodcastNs + "locked")!.Value.Should().Be("yes");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_EpisodeEnclosure()
    {
        var podcast = CreateTestPodcast();
        var episode = CreateTestEpisode();
        var episodes = new List<EpisodeMetadata> { episode };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;
        var enclosure = item.Element("enclosure")!;

        enclosure.Attribute("url")!.Value.Should().Be(episode.AudioUrl);
        enclosure.Attribute("length")!.Value.Should().Be(episode.AudioSizeBytes.ToString());
        enclosure.Attribute("type")!.Value.Should().Be("audio/x-m4a");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_ItunesDurationFormat()
    {
        var podcast = CreateTestPodcast();
        var episode = CreateTestEpisode() with { Duration = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(23) + TimeSpan.FromSeconds(45) };
        var episodes = new List<EpisodeMetadata> { episode };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;

        item.Element(Itunes + "duration")!.Value.Should().Be("01:23:45");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_PubDateRfc2822Format()
    {
        var podcast = CreateTestPodcast();
        var episode = CreateTestEpisode();
        var episodes = new List<EpisodeMetadata> { episode };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;

        var pubDate = item.Element("pubDate")!.Value;
        pubDate.Should().Contain("Mon, 15 Jan 2024");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_PodloveChapters()
    {
        var podcast = CreateTestPodcast();
        var episode = CreateTestEpisode() with
        {
            Chapters =
            [
                new ChapterMark { Title = "Intro", StartTime = TimeSpan.Zero },
                new ChapterMark { Title = "Main Content", StartTime = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30) },
            ],
        };
        var episodes = new List<EpisodeMetadata> { episode };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;
        var chapters = item.Element(Psc + "chapters")!;

        chapters.Attribute("version")!.Value.Should().Be("1.2");
        var chapterElements = chapters.Elements(Psc + "chapter").ToList();
        chapterElements.Should().HaveCount(2);
        chapterElements[0].Attribute("start")!.Value.Should().Be("00:00:00.000");
        chapterElements[0].Attribute("title")!.Value.Should().Be("Intro");
        chapterElements[1].Attribute("start")!.Value.Should().Be("00:02:30.000");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_ChapterWithLinkAndImage()
    {
        var podcast = CreateTestPodcast();
        var episode = CreateTestEpisode() with
        {
            Chapters =
            [
                new ChapterMark
                {
                    Title = "Chapter with extras",
                    StartTime = TimeSpan.Zero,
                    LinkUrl = "https://example.com/link",
                    ImageUrl = "https://example.com/chapter.jpg",
                },
            ],
        };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, [episode]);
        var doc = XDocument.Parse(xml);
        var chapter = doc.Descendants(Psc + "chapter").First();

        chapter.Attribute("href")!.Value.Should().Be("https://example.com/link");
        chapter.Attribute("image")!.Value.Should().Be("https://example.com/chapter.jpg");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_EpisodeWithoutChapters_NoChaptersElement()
    {
        var podcast = CreateTestPodcast();
        var episode = CreateTestEpisode();

        var xml = await _generator.GenerateFeedXmlAsync(podcast, [episode]);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;

        item.Element(Psc + "chapters").Should().BeNull();
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_EmptyEpisodes_ValidFeed()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeMetadata>();

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);

        var doc = XDocument.Parse(xml);
        doc.Root!.Element("channel")!.Elements("item").Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_SpecialCharacters_Escaped()
    {
        var podcast = CreateTestPodcast() with
        {
            Title = "Podcast & Friends <Special>",
            Description = "Description with \"quotes\" & <tags>",
        };
        var episodes = new List<EpisodeMetadata>();

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);

        // Should be valid XML (parsing would fail if not escaped)
        var doc = XDocument.Parse(xml);
        doc.Root!.Element("channel")!.Element("title")!.Value.Should().Be("Podcast & Friends <Special>");
    }

    [Fact]
    public async Task GenerateFeedXmlAsync_GuidNotPermaLink()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeMetadata> { CreateTestEpisode() };

        var xml = await _generator.GenerateFeedXmlAsync(podcast, episodes);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;
        var guid = item.Element("guid")!;

        guid.Attribute("isPermaLink")!.Value.Should().Be("false");
        guid.Value.Should().Be("ep1");
    }
}
