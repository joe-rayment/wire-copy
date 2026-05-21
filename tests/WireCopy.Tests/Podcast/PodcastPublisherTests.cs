// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class PodcastPublisherTests : IDisposable
{
    private readonly ICloudStorageClient _storage;
    private readonly IPodcastFeedGenerator _feedGenerator;
    private readonly IFeedReachabilityProbe _reachability;
    private readonly PodcastPublisher _publisher;
    private readonly List<string> _tempFiles = [];

    public PodcastPublisherTests()
    {
        _storage = Substitute.For<ICloudStorageClient>();
        _feedGenerator = Substitute.For<IPodcastFeedGenerator>();
        _reachability = Substitute.For<IFeedReachabilityProbe>();
        _reachability.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(FeedReachabilityResult.Ok());
        _publisher = new PodcastPublisher(
            _storage,
            _feedGenerator,
            NullLogger<PodcastPublisher>.Instance,
            _reachability);

        // Default setup: no existing feed index
        _storage.DownloadStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        // Default setup: return a URL for any upload
        _storage.UploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
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
            Arg.Is<string>(s => s.Contains("episodes/") && s.EndsWith(".m4a")),
            "audio/x-m4a",
            Arg.Any<IProgress<long>?>(),
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
    public async Task PublishFeedAsync_AlreadyUploadedEpisode_SameSize_Skipped()
    {
        // workspace-z2om: the upload-skip optimization is keyed on REMOTE size
        // matching LOCAL size, not on existence alone. A local temp file is
        // 1024 bytes (CreateTestEpisode writes that), so we mock the remote
        // size to match exactly.
        _storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _storage.GetObjectSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1024L);

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();
        result.EpisodesPublished.Should().Be(0);

        await _storage.DidNotReceive().UploadAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("episodes/")),
            Arg.Any<string>(),
            Arg.Any<IProgress<long>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishFeedAsync_RemoteSizeDiffersFromLocal_ReUploadsAudio()
    {
        // workspace-z2om regression: same deterministic id but the local
        // M4B has changed (e.g. a second article was appended). The publisher
        // MUST re-upload so the audio matches the new feed.xml metadata.
        _storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _storage.GetObjectSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(512L); // stale remote — local is 1024

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();
        result.EpisodesPublished.Should().Be(1, because: "size mismatch forces re-upload");

        await _storage.Received(1).UploadAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("episodes/") && s.EndsWith(".m4a")),
            "audio/x-m4a",
            Arg.Any<IProgress<long>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishFeedAsync_UploadFailure_ReturnsFailure()
    {
        _storage.UploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("Upload failed"));

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { CreateTestEpisode() };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Upload failed");
    }

    [Fact]
    public async Task PublishFeedAsync_AllAudioFilesMissing_FailsWithNoAudioFiles()
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

        result.Success.Should().BeFalse();
        result.FailureClass.Should().Be(FeedPublishFailureClass.NoAudioFiles);
        result.SkippedEpisodes.Should().HaveCount(1);
        result.SkippedEpisodes[0].Title.Should().Be("Missing Episode");

        // workspace-mie2: when all episodes skip, feed.xml is NOT overwritten
        await _storage.DidNotReceive().UploadStringAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.EndsWith("feed.xml")),
            "application/rss+xml",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishFeedAsync_OneOfTwoAudioFilesMissing_ReturnsPartial()
    {
        var goodEpisode = CreateTestEpisode("Good Episode", "https://example.com/good");
        var badEpisode = new EpisodeSource
        {
            Title = "Bad Episode",
            Description = "Has a missing file",
            LocalAudioFilePath = "/nonexistent/path/audio.m4b",
            Duration = TimeSpan.FromMinutes(5),
            SourceUrl = "https://example.com/missing",
        };

        var podcast = CreateTestPodcast();
        var episodes = new List<EpisodeSource> { goodEpisode, badEpisode };

        var result = await _publisher.PublishFeedAsync(podcast, episodes);

        result.Success.Should().BeTrue();
        result.EpisodesPublished.Should().Be(1);
        result.SkippedEpisodes.Should().HaveCount(1);
        result.SkippedEpisodes[0].Title.Should().Be("Bad Episode");
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

        _storage.UploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string>(1);
                uploadPaths.Add(path);
                return $"https://storage.example.com/{path}";
            });

        await _publisher.PublishFeedAsync(podcast, [episode1]);

        // Second publish of same episode: ExistsAsync returns true AND the
        // remote size matches the local size (1024 — see CreateTestEpisode),
        // so the publisher skips the re-upload (workspace-z2om).
        _storage.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _storage.GetObjectSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1024L);

        await _publisher.PublishFeedAsync(podcast, [episode2]);

        // Should have uploaded exactly once (second was skipped because both
        // existence and size matched)
        var episodePaths = uploadPaths.Where(p => p.Contains("episodes/")).ToList();
        episodePaths.Should().HaveCount(1);
    }

    /// <summary>
    /// workspace-p1px: when the post-publish reachability probe returns
    /// BucketNotPublic (the bucket lacks allUsers:objectViewer), the publisher
    /// auto-attempts MakeBucketPublicAsync. If the SA succeeds in flipping the
    /// binding, a second probe must run and a passing result must promote the
    /// publish back to a full success.
    /// </summary>
    [Fact]
    public async Task PublishFeedAsync_BucketNotPublic_HelperSucceeds_RetryProbePasses_Successful()
    {
        _storage.BucketName.Returns("my-private-bucket");
        _storage.MakeBucketPublicAsync("my-private-bucket", Arg.Any<CancellationToken>())
            .Returns(MakeBucketPublicResult.Success());

        // First probe → 403 (bucket private). Second probe (after remediation)
        // → Ok. NSubstitute returns values in order across successive calls.
        _reachability.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new FeedReachabilityResult { FailureClass = FeedPublishFailureClass.BucketNotPublic, Diagnostic = "anonymous GET returned 403", HttpStatusCode = 403 },
                FeedReachabilityResult.Ok());

        var result = await _publisher.PublishFeedAsync(
            CreateTestPodcast(),
            [CreateTestEpisode()]);

        result.Success.Should().BeTrue(
            "after MakeBucketPublic succeeds, the retried probe sees a public bucket and the publish is fully successful");
        await _storage.Received(1).MakeBucketPublicAsync(
            "my-private-bucket", Arg.Any<CancellationToken>());
        await _reachability.Received(2).CheckAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// workspace-p1px: when the helper reports PermissionDenied (SA lacks
    /// setIamPolicy), the publisher must keep the BucketNotPublic verdict so
    /// the result screen renders the gsutil one-liner instead of pretending
    /// nothing went wrong.
    /// </summary>
    [Fact]
    public async Task PublishFeedAsync_BucketNotPublic_HelperPermissionDenied_StaysBucketNotPublic()
    {
        _storage.BucketName.Returns("my-private-bucket");
        _storage.MakeBucketPublicAsync("my-private-bucket", Arg.Any<CancellationToken>())
            .Returns(MakeBucketPublicResult.PermissionDenied(
                "Permission iam.serviceAccounts.actAs denied"));

        _reachability.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FeedReachabilityResult { FailureClass = FeedPublishFailureClass.BucketNotPublic, Diagnostic = "anonymous GET returned 403", HttpStatusCode = 403 });

        var result = await _publisher.PublishFeedAsync(
            CreateTestPodcast(),
            [CreateTestEpisode()]);

        result.Success.Should().BeFalse();
        result.FailureClass.Should().Be(FeedPublishFailureClass.BucketNotPublic);
        await _storage.Received(1).MakeBucketPublicAsync(
            "my-private-bucket", Arg.Any<CancellationToken>());
        // Probe must NOT be re-run on permission denied — the result is the
        // probe's original 403 diagnostic.
        await _reachability.Received(1).CheckAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// workspace-p1px: when the bucket is already public on the IAM side
    /// (helper returns AlreadyPublic) but the public GET still returns 403,
    /// the retry probe is run anyway — the second probe's verdict is the
    /// load-bearing result. This handles the edge case where the GCS API
    /// reports the binding present while propagation hasn't reached the
    /// public edge yet.
    /// </summary>
    [Fact]
    public async Task PublishFeedAsync_BucketNotPublic_HelperAlreadyPublic_RetriesProbeAndUsesResult()
    {
        _storage.BucketName.Returns("my-public-bucket");
        _storage.MakeBucketPublicAsync("my-public-bucket", Arg.Any<CancellationToken>())
            .Returns(MakeBucketPublicResult.AlreadyPublic());

        // First probe → 403 (stale propagation), second probe → still 403.
        _reachability.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new FeedReachabilityResult { FailureClass = FeedPublishFailureClass.BucketNotPublic, Diagnostic = "anonymous GET returned 403", HttpStatusCode = 403 },
                new FeedReachabilityResult { FailureClass = FeedPublishFailureClass.BucketNotPublic, Diagnostic = "anonymous GET returned 403", HttpStatusCode = 403 });

        var result = await _publisher.PublishFeedAsync(
            CreateTestPodcast(),
            [CreateTestEpisode()]);

        result.Success.Should().BeFalse();
        result.FailureClass.Should().Be(FeedPublishFailureClass.BucketNotPublic);
        await _reachability.Received(2).CheckAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// workspace-p1px: when the storage client reports no BucketName (e.g.
    /// the user is mid-setup), don't attempt MakeBucketPublic at all — we
    /// have nothing to pass it. The original probe verdict stands.
    /// </summary>
    [Fact]
    public async Task PublishFeedAsync_BucketNotPublic_EmptyBucketName_SkipsRemediation()
    {
        _storage.BucketName.Returns(string.Empty);
        _reachability.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FeedReachabilityResult { FailureClass = FeedPublishFailureClass.BucketNotPublic, Diagnostic = "anonymous GET returned 403", HttpStatusCode = 403 });

        var result = await _publisher.PublishFeedAsync(
            CreateTestPodcast(),
            [CreateTestEpisode()]);

        result.Success.Should().BeFalse();
        result.FailureClass.Should().Be(FeedPublishFailureClass.BucketNotPublic);
        await _storage.DidNotReceive().MakeBucketPublicAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
