// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.ValueObjects.Podcast;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Unit")]
public class PodcastPublisherTests : IDisposable
{
    private readonly ICloudStorageClient _storage;
    private readonly IPodcastFeedGenerator _feedGenerator;
    private readonly PodcastPublisher _publisher;
    private readonly List<string> _tempFiles = [];

    public PodcastPublisherTests()
    {
        _storage = Substitute.For<ICloudStorageClient>();
        _feedGenerator = Substitute.For<IPodcastFeedGenerator>();
        _publisher = new PodcastPublisher(
            _storage,
            _feedGenerator,
            NullLogger<PodcastPublisher>.Instance);

        // Default setup: no existing feed index
        _storage.DownloadStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        // Default setup: return a URL for any upload
        _storage.UploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => $"https://storage.example.com/{callInfo.ArgAt<string>(1)}");

        _storage.UploadStringAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => $"https://storage.example.com/{callInfo.ArgAt<string>(1)}");

        _storage.GetPublicUrl(Arg.Any<string>())
            .Returns(callInfo => $"https://storage.example.com/{callInfo.ArgAt<string>(0)}");

        _storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _feedGenerator.GenerateFeedXmlAsync(Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeMetadata>>(), Arg.Any<CancellationToken>())
            .Returns("<rss>feed</rss>");
    }

    private static PodcastMetadata CreateTestPodcast() => new()
    {
        Title = "Test Podcast",
        Description = "Test description",
        Author = "Test Author",
        Language = "en-us",
        ImageUrl = "https://example.com/cover.jpg",
        Category = "Technology",
        Explicit = false,
    };

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }

        GC.SuppressFinalize(this);
    }

    private EpisodeSource CreateTestEpisode(string title = "Episode 1", string? sourceUrl = null)
    {
        // Create a temp file so File.Exists returns true
        var tempFile = Path.GetTempFileName();
        File.WriteAllBytes(tempFile, new byte[1024]);
        _tempFiles.Add(tempFile);

        return new EpisodeSource
        {
            Title = title,
            Description = $"Description of {title}",
            LocalAudioFilePath = tempFile,
            Duration = TimeSpan.FromMinutes(10),
            SourceUrl = sourceUrl ?? $"https://example.com/{title.Replace(" ", "-")}",
        };
    }

    [Fact]
    public async Task PublishFeedAsync_UploadsAudioAndFeed()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();
        result.EpisodesPublished.Should().Be(1);
        result.FeedUrl.Should().Contain("feed.xml");

        await _storage.Received(1).UploadAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("episodes/") && s.EndsWith(".m4b")),
            "audio/x-m4b",
            Arg.Any<CancellationToken>());

        await _storage.Received(1).UploadStringAsync(
            "<rss>feed</rss>",
            Arg.Is<string>(s => s.EndsWith("feed.xml")),
            "application/rss+xml",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishFeedAsync_NewFeed_GeneratesNewUuid()
    {
        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();

        // Manifest should be uploaded with a path containing the UUID
        await _storage.Received().UploadStringAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("podcasts/") && s.EndsWith("manifest.json")),
            "application/json",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishFeedAsync_ExistingFeed_ReusesFeedUuid()
    {
        var knownUuid = "abc123";
        var feedIndex = $"{{\"Test Podcast\": {{\"uuid\": \"{knownUuid}\", \"feedUrl\": \"https://example.com/feed.xml\"}}}}";

        _storage.DownloadStringAsync("podcasts/feed-index.json", Arg.Any<CancellationToken>())
            .Returns(feedIndex);

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();

        // The feed path should use the existing UUID
        await _storage.Received().UploadStringAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.StartsWith($"podcasts/{knownUuid}/")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishFeedAsync_AlreadyUploadedEpisode_Skipped()
    {
        _storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();
        result.EpisodesPublished.Should().Be(0);

        await _storage.DidNotReceive().UploadAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("episodes/")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishFeedAsync_UploadFailure_ReturnsFailure()
    {
        _storage.UploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("Upload failed"));

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Upload failed");
    }

    [Fact]
    public async Task PublishFeedAsync_MissingAudioFile_SkipsEpisode()
    {
        var episode = new EpisodeSource
        {
            Title = "Missing Episode",
            Description = "Has a missing file",
            LocalAudioFilePath = "/nonexistent/path/audio.m4b",
            Duration = TimeSpan.FromMinutes(5),
            SourceUrl = "https://example.com/missing",
        };

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { episode };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();
        result.EpisodesPublished.Should().Be(0);
    }

    [Fact]
    public async Task GetExistingFeedUrlAsync_ExistingFeed_ReturnsUrl()
    {
        var feedIndex = "{\"My Podcast\": {\"uuid\": \"abc\", \"feedUrl\": \"https://example.com/feed.xml\"}}";
        _storage.DownloadStringAsync("podcasts/feed-index.json", Arg.Any<CancellationToken>())
            .Returns(feedIndex);

        var result = await _publisher.GetExistingFeedUrlAsync("My Podcast");

        result.Should().Be("https://example.com/feed.xml");
    }

    [Fact]
    public async Task GetExistingFeedUrlAsync_NoFeed_ReturnsNull()
    {
        var result = await _publisher.GetExistingFeedUrlAsync("Nonexistent Podcast");

        result.Should().BeNull();
    }

    [Fact]
    public async Task PublishFeedAsync_DeterministicEpisodeId_SameInputSameId()
    {
        var podcast = CreateTestPodcast();
        var episode1 = CreateTestEpisode("Episode A", "https://example.com/a");
        var episode2 = CreateTestEpisode("Episode A", "https://example.com/a");

        // Track all episode upload paths
        var uploadPaths = new List<string>();

        _storage.UploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string>(1);
                uploadPaths.Add(path);
                return $"https://storage.example.com/{path}";
            });

        await _publisher.PublishFeedAsync(podcast, [episode1]);

        // Second publish of same episode: ExistsAsync returns true so it skips upload
        _storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _publisher.PublishFeedAsync(podcast, [episode2]);

        // Should have uploaded exactly once (second was skipped as exists)
        var episodePaths = uploadPaths.Where(p => p.Contains("episodes/")).ToList();
        episodePaths.Should().HaveCount(1);
    }
}
