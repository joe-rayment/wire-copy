// <copyright file="RssFeedGeneratorTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests;

public class RssFeedGeneratorTests
{
    private readonly RssFeedGenerator _generator;
    private readonly ILogger<RssFeedGenerator> _logger;

    public RssFeedGeneratorTests()
    {
        _logger = Substitute.For<ILogger<RssFeedGenerator>>();
        _generator = new RssFeedGenerator(_logger);
    }

    [Fact]
    public void CreateOrderedEpisodes_FirstArticleGetsNewestPubDate()
    {
        // Arrange
        var baseTime = new DateTime(2025, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        var articles = new List<(string Title, string Description, string FileName, long FileSize, int DurationSecs, string Guid, DateTime ArticleDate)>
        {
            ("First Article", "Description 1", "file1.mp3", 1000, 60, "guid1", DateTime.Today),
            ("Second Article", "Description 2", "file2.mp3", 2000, 120, "guid2", DateTime.Today),
            ("Third Article", "Description 3", "file3.mp3", 3000, 180, "guid3", DateTime.Today),
        };

        // Act
        var episodes = RssFeedGenerator.CreateOrderedEpisodes(articles, baseTime).ToList();

        // Assert
        episodes.Should().HaveCount(3);

        // First article should have the newest (base) time
        episodes[0].PubDate.Should().Be(baseTime);
        episodes[0].Title.Should().Be("First Article");

        // Second article should be 1 second earlier
        episodes[1].PubDate.Should().Be(baseTime.AddSeconds(-1));
        episodes[1].Title.Should().Be("Second Article");

        // Third article should be 2 seconds earlier
        episodes[2].PubDate.Should().Be(baseTime.AddSeconds(-2));
        episodes[2].Title.Should().Be("Third Article");
    }

    [Fact]
    public void CreateOrderedEpisodes_PubDatesAreDescending()
    {
        // Arrange - this is critical for RSS feeds to show correct order in podcast apps
        var baseTime = new DateTime(2025, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        var articles = new List<(string Title, string Description, string FileName, long FileSize, int DurationSecs, string Guid, DateTime ArticleDate)>
        {
            ("Article 1", "Desc", "f1.mp3", 1000, 60, "g1", DateTime.Today),
            ("Article 2", "Desc", "f2.mp3", 1000, 60, "g2", DateTime.Today),
            ("Article 3", "Desc", "f3.mp3", 1000, 60, "g3", DateTime.Today),
            ("Article 4", "Desc", "f4.mp3", 1000, 60, "g4", DateTime.Today),
            ("Article 5", "Desc", "f5.mp3", 1000, 60, "g5", DateTime.Today),
        };

        // Act
        var episodes = RssFeedGenerator.CreateOrderedEpisodes(articles, baseTime).ToList();

        // Assert - each subsequent episode should have an earlier pubDate
        for (int i = 1; i < episodes.Count; i++)
        {
            episodes[i].PubDate.Should().BeBefore(episodes[i - 1].PubDate,
                $"Episode {i + 1} should have an earlier pubDate than episode {i}");
        }
    }

    [Fact]
    public void GenerateFeed_EpisodesAppearInCorrectOrder_FirstArticleFirst()
    {
        // Arrange
        var baseTime = new DateTime(2025, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        var articles = new List<(string Title, string Description, string FileName, long FileSize, int DurationSecs, string Guid, DateTime ArticleDate)>
        {
            ("Headlines Today", "Desc 1", "headlines.mp3", 1000, 60, "guid-headlines", DateTime.Today),
            ("Breaking News", "Desc 2", "breaking.mp3", 2000, 120, "guid-breaking", DateTime.Today),
            ("Opinion Piece", "Desc 3", "opinion.mp3", 3000, 180, "guid-opinion", DateTime.Today),
        };

        var episodes = RssFeedGenerator.CreateOrderedEpisodes(articles, baseTime).ToList();
        var podcastInfo = new PodcastInfo("NYT Daily", "Daily news", "NYT", "en-us");

        // Act
        var feedXml = _generator.GenerateFeed(episodes, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        var items = doc.Descendants("item").ToList();

        // Assert
        items.Should().HaveCount(3);

        // Verify the first item in the feed is "Headlines Today" (first article)
        var firstItemTitle = items[0].Element("title")?.Value;
        firstItemTitle.Should().Be("Headlines Today",
            "First article from NYT page should appear first in the RSS feed");

        var secondItemTitle = items[1].Element("title")?.Value;
        secondItemTitle.Should().Be("Breaking News");

        var thirdItemTitle = items[2].Element("title")?.Value;
        thirdItemTitle.Should().Be("Opinion Piece");
    }

    [Fact]
    public void GenerateFeed_PubDatesInRssFeed_AreDescending()
    {
        // Arrange
        var baseTime = new DateTime(2025, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        var articles = new List<(string Title, string Description, string FileName, long FileSize, int DurationSecs, string Guid, DateTime ArticleDate)>
        {
            ("Article 1", "Desc", "f1.mp3", 1000, 60, "g1", DateTime.Today),
            ("Article 2", "Desc", "f2.mp3", 1000, 60, "g2", DateTime.Today),
            ("Article 3", "Desc", "f3.mp3", 1000, 60, "g3", DateTime.Today),
        };

        var episodes = RssFeedGenerator.CreateOrderedEpisodes(articles, baseTime).ToList();
        var podcastInfo = new PodcastInfo("Test", "Test", "Test");

        // Act
        var feedXml = _generator.GenerateFeed(episodes, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        var items = doc.Descendants("item").ToList();

        // Assert - verify pubDate elements have descending times
        var pubDates = items.Select(item => item.Element("pubDate")?.Value).ToList();
        pubDates.Should().HaveCount(3);

        // First pubDate should contain "10:00:00"
        pubDates[0].Should().Contain("10:00:00");
        // Second pubDate should contain "09:59:59"
        pubDates[1].Should().Contain("09:59:59");
        // Third pubDate should contain "09:59:58"
        pubDates[2].Should().Contain("09:59:58");
    }

    [Fact]
    public void GenerateFeed_ContainsRequiredItunesElements()
    {
        // Arrange
        var episodes = new List<PodcastEpisode>
        {
            new("Test Episode", "Test Desc", "test.mp3", DateTime.UtcNow, 1000, 60, "guid1"),
        };
        var podcastInfo = new PodcastInfo("My Podcast", "A test podcast", "Test Author", "en-us");

        // Act
        var feedXml = _generator.GenerateFeed(episodes, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";

        // Assert
        var channel = doc.Descendants("channel").First();
        channel.Element(itunes + "author")?.Value.Should().Be("Test Author");
        channel.Element(itunes + "explicit")?.Value.Should().Be("no");
        channel.Element(itunes + "category")?.Attribute("text")?.Value.Should().Be("News");
    }

    [Fact]
    public void GenerateFeed_EnclosureHasPlaceholderUrl()
    {
        // Arrange
        var episodes = new List<PodcastEpisode>
        {
            new("Test Episode", "Test Desc", "my-audio-file.mp3", DateTime.UtcNow, 12345, 300, "guid1"),
        };
        var podcastInfo = new PodcastInfo("Test", "Test", "Test");

        // Act
        var feedXml = _generator.GenerateFeed(episodes, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        var enclosure = doc.Descendants("enclosure").First();

        // Assert
        enclosure.Attribute("url")?.Value.Should().Be("{{BASE_URL}}/my-audio-file.mp3");
        enclosure.Attribute("length")?.Value.Should().Be("12345");
        enclosure.Attribute("type")?.Value.Should().Be("audio/mpeg");
    }

    [Fact]
    public void GenerateFeed_DurationFormattedCorrectly()
    {
        // Arrange
        var episodes = new List<PodcastEpisode>
        {
            new("Test Episode", "Test Desc", "test.mp3", DateTime.UtcNow, 1000, 3661, "guid1"), // 1 hour, 1 minute, 1 second
        };
        var podcastInfo = new PodcastInfo("Test", "Test", "Test");

        // Act
        var feedXml = _generator.GenerateFeed(episodes, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        var duration = doc.Descendants(itunes + "duration").First().Value;

        // Assert
        duration.Should().Be("01:01:01");
    }

    [Fact]
    public void CreateOrderedEpisodes_EmptyList_ReturnsEmpty()
    {
        // Arrange
        var articles = new List<(string Title, string Description, string FileName, long FileSize, int DurationSecs, string Guid, DateTime ArticleDate)>();

        // Act
        var episodes = RssFeedGenerator.CreateOrderedEpisodes(articles).ToList();

        // Assert
        episodes.Should().BeEmpty();
    }

    [Fact]
    public void CreateOrderedEpisodes_SingleArticle_HasBaseTime()
    {
        // Arrange
        var baseTime = new DateTime(2025, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var articles = new List<(string Title, string Description, string FileName, long FileSize, int DurationSecs, string Guid, DateTime ArticleDate)>
        {
            ("Only Article", "Desc", "only.mp3", 1000, 60, "guid", DateTime.Today),
        };

        // Act
        var episodes = RssFeedGenerator.CreateOrderedEpisodes(articles, baseTime).ToList();

        // Assert
        episodes.Should().HaveCount(1);
        episodes[0].PubDate.Should().Be(baseTime);
    }

    [Fact]
    public void GenerateCombinedFeed_ContainsPodcastNamespace()
    {
        // Arrange
        var episode = new CombinedPodcastEpisode(
            Title: "Today's Headlines",
            Description: "Your daily news digest",
            AudioFileName: "combined.m4a",
            ChaptersFileName: "chapters.json",
            PubDate: DateTime.UtcNow,
            FileSizeBytes: 5000000,
            DurationSeconds: 1800,
            Guid: "combined-guid-123",
            ChapterTitles: new List<string> { "Article 1", "Article 2" });
        var podcastInfo = new PodcastInfo("Test Podcast", "Test Description", "Test Author");

        // Act
        var feedXml = _generator.GenerateCombinedFeed(episode, podcastInfo);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var rss = doc.Root;
        rss.Should().NotBeNull();

        var podcastNs = rss!.Attribute(XNamespace.Xmlns + "podcast")?.Value;
        podcastNs.Should().Be("https://podcastindex.org/namespace/1.0");
    }

    [Fact]
    public void GenerateCombinedFeed_HasSingleItemWithChaptersTag()
    {
        // Arrange
        var episode = new CombinedPodcastEpisode(
            Title: "Today's Headlines",
            Description: "Your daily news digest",
            AudioFileName: "combined.m4a",
            ChaptersFileName: "chapters.json",
            PubDate: DateTime.UtcNow,
            FileSizeBytes: 5000000,
            DurationSeconds: 1800,
            Guid: "combined-guid-123",
            ChapterTitles: new List<string> { "Article 1", "Article 2", "Article 3" });
        var podcastInfo = new PodcastInfo("Test Podcast", "Test Description", "Test Author");

        // Act
        var feedXml = _generator.GenerateCombinedFeed(episode, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        XNamespace podcast = "https://podcastindex.org/namespace/1.0";

        // Assert
        var items = doc.Descendants("item").ToList();
        items.Should().HaveCount(1, "Combined feed should have exactly one episode");

        var chapters = items[0].Element(podcast + "chapters");
        chapters.Should().NotBeNull("Episode should have podcast:chapters element");
        chapters!.Attribute("url")?.Value.Should().Be("{{BASE_URL}}/chapters.json");
        chapters.Attribute("type")?.Value.Should().Be("application/json+chapters");
    }

    [Fact]
    public void GenerateCombinedFeed_DescriptionListsChapterTitles()
    {
        // Arrange
        var episode = new CombinedPodcastEpisode(
            Title: "Today's Headlines",
            Description: "Your daily news digest",
            AudioFileName: "combined.m4a",
            ChaptersFileName: "chapters.json",
            PubDate: DateTime.UtcNow,
            FileSizeBytes: 5000000,
            DurationSeconds: 1800,
            Guid: "combined-guid-123",
            ChapterTitles: new List<string> { "Breaking News Story", "Opinion: Market Analysis", "Sports Update" });
        var podcastInfo = new PodcastInfo("Test Podcast", "Test Description", "Test Author");

        // Act
        var feedXml = _generator.GenerateCombinedFeed(episode, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        var description = doc.Descendants("item").First().Element("description")?.Value;

        // Assert
        description.Should().Contain("Articles in this episode:");
        description.Should().Contain("1. Breaking News Story");
        description.Should().Contain("2. Opinion: Market Analysis");
        description.Should().Contain("3. Sports Update");
    }

    [Fact]
    public void GenerateCombinedFeed_EnclosureUsesM4aMimeType()
    {
        // Arrange
        var episode = new CombinedPodcastEpisode(
            Title: "Today's Headlines",
            Description: "Your daily news digest",
            AudioFileName: "combined.m4a",
            ChaptersFileName: "chapters.json",
            PubDate: DateTime.UtcNow,
            FileSizeBytes: 12345678,
            DurationSeconds: 3600,
            Guid: "combined-guid-123",
            ChapterTitles: new List<string> { "Article 1" });
        var podcastInfo = new PodcastInfo("Test Podcast", "Test Description", "Test Author");

        // Act
        var feedXml = _generator.GenerateCombinedFeed(episode, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        var enclosure = doc.Descendants("enclosure").First();

        // Assert
        enclosure.Attribute("url")?.Value.Should().Be("{{BASE_URL}}/combined.m4a");
        enclosure.Attribute("length")?.Value.Should().Be("12345678");
        enclosure.Attribute("type")?.Value.Should().Be("audio/mp4", "M4A files use audio/mp4 MIME type");
    }

    [Fact]
    public void GenerateCombinedFeed_HasPodcastLockedTag()
    {
        // Arrange
        var episode = new CombinedPodcastEpisode(
            Title: "Today's Headlines",
            Description: "Your daily news digest",
            AudioFileName: "combined.m4a",
            ChaptersFileName: "chapters.json",
            PubDate: DateTime.UtcNow,
            FileSizeBytes: 5000000,
            DurationSeconds: 1800,
            Guid: "combined-guid-123",
            ChapterTitles: new List<string> { "Article 1" });
        var podcastInfo = new PodcastInfo("Test Podcast", "Test Description", "Test Author");

        // Act
        var feedXml = _generator.GenerateCombinedFeed(episode, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        XNamespace podcast = "https://podcastindex.org/namespace/1.0";

        // Assert
        var channel = doc.Descendants("channel").First();
        var locked = channel.Element(podcast + "locked");
        locked.Should().NotBeNull("Combined feed should have podcast:locked element");
        locked!.Value.Should().Be("no");
    }

    [Fact]
    public void GenerateCombinedFeed_DurationFormattedCorrectly()
    {
        // Arrange - 1 hour 30 minutes 45 seconds = 5445 seconds
        var episode = new CombinedPodcastEpisode(
            Title: "Today's Headlines",
            Description: "Your daily news digest",
            AudioFileName: "combined.m4a",
            ChaptersFileName: "chapters.json",
            PubDate: DateTime.UtcNow,
            FileSizeBytes: 5000000,
            DurationSeconds: 5445,
            Guid: "combined-guid-123",
            ChapterTitles: new List<string> { "Article 1" });
        var podcastInfo = new PodcastInfo("Test Podcast", "Test Description", "Test Author");

        // Act
        var feedXml = _generator.GenerateCombinedFeed(episode, podcastInfo);
        var doc = XDocument.Parse(feedXml);
        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        var duration = doc.Descendants(itunes + "duration").First().Value;

        // Assert
        duration.Should().Be("01:30:45");
    }
}
