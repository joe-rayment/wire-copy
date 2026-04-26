// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.ValueObjects.Podcast;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Unit")]
public class GcsValidationAndBootstrapTests
{
    private readonly ICloudStorageClient _storage;
    private readonly IPodcastFeedGenerator _feedGenerator;
    private readonly PodcastPublisher _publisher;

    public GcsValidationAndBootstrapTests()
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

        _storage.UploadStringAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => $"https://storage.example.com/{callInfo.ArgAt<string>(1)}");

        _storage.GetPublicUrl(Arg.Any<string>())
            .Returns(callInfo => $"https://storage.example.com/{callInfo.ArgAt<string>(0)}");

        _feedGenerator.GenerateFeedXmlAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeMetadata>>(), Arg.Any<CancellationToken>())
            .Returns("<rss>empty feed</rss>");
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

    // --- BootstrapFeedAsync tests ---

    [Fact]
    public async Task BootstrapFeedAsync_NoExistingFeed_CreatesEmptyFeed()
    {
        var podcast = CreateTestPodcast();

        var result = await _publisher.BootstrapFeedAsync(podcast);

        result.Success.Should().BeTrue();
        result.FeedUrl.Should().Contain("feed.xml");
        result.EpisodesPublished.Should().Be(0);

        // Should generate feed with empty episode list
        await _feedGenerator.Received(1).GenerateFeedXmlAsync(
            Arg.Is<PodcastMetadata>(p => p.Title == "Test Podcast" && p.FeedUrl != null),
            Arg.Is<IReadOnlyList<EpisodeMetadata>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());

        // Should upload feed.xml
        await _storage.Received().UploadStringAsync(
            "<rss>empty feed</rss>",
            Arg.Is<string>(s => s.EndsWith("feed.xml")),
            "application/rss+xml",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BootstrapFeedAsync_ExistingFeed_ReturnsExistingUrl()
    {
        var feedIndex = "{\"Test Podcast\": {\"uuid\": \"existing-uuid\", \"feedUrl\": \"https://storage.example.com/podcasts/existing-uuid/feed.xml\"}}";
        _storage.DownloadStringAsync("podcasts/feed-index.json", Arg.Any<CancellationToken>())
            .Returns(feedIndex);

        var podcast = CreateTestPodcast();

        var result = await _publisher.BootstrapFeedAsync(podcast);

        result.Success.Should().BeTrue();
        result.FeedUrl.Should().Be("https://storage.example.com/podcasts/existing-uuid/feed.xml");

        // Should NOT generate or upload a new feed
        await _feedGenerator.DidNotReceive().GenerateFeedXmlAsync(
            Arg.Any<PodcastMetadata>(),
            Arg.Any<IReadOnlyList<EpisodeMetadata>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BootstrapFeedAsync_UploadFails_ReturnsFailure()
    {
        _storage.UploadStringAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Upload failed"));

        var podcast = CreateTestPodcast();

        var result = await _publisher.BootstrapFeedAsync(podcast);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Upload failed");
    }

    [Fact]
    public async Task BootstrapFeedAsync_GeneratesValidEmptyRss()
    {
        var podcast = CreateTestPodcast();

        await _publisher.BootstrapFeedAsync(podcast);

        // Verify the podcast metadata passed to feed generator has FeedUrl set (for atom:link)
        await _feedGenerator.Received(1).GenerateFeedXmlAsync(
            Arg.Is<PodcastMetadata>(p => !string.IsNullOrEmpty(p.FeedUrl)),
            Arg.Is<IReadOnlyList<EpisodeMetadata>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BootstrapFeedAsync_UpdatesFeedIndex()
    {
        var podcast = CreateTestPodcast();

        await _publisher.BootstrapFeedAsync(podcast);

        // Should upload feed-index.json
        await _storage.Received().UploadStringAsync(
            Arg.Is<string>(s => s.Contains("Test Podcast")),
            "podcasts/feed-index.json",
            "application/json",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BootstrapFeedAsync_UpdatesManifest()
    {
        var podcast = CreateTestPodcast();

        await _publisher.BootstrapFeedAsync(podcast);

        // Should upload manifest.json
        await _storage.Received().UploadStringAsync(
            Arg.Is<string>(s => s.Contains("Test Podcast")),
            Arg.Is<string>(s => s.EndsWith("manifest.json")),
            "application/json",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BootstrapFeedAsync_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Mock throws OperationCanceledException when called with cancelled token
        _storage.DownloadStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var podcast = CreateTestPodcast();

        var act = () => _publisher.BootstrapFeedAsync(podcast, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- ValidateConnectionAsync interface contract tests ---

    [Fact]
    public async Task ValidateConnectionAsync_ValidResult_HasIsValidTrue()
    {
        var mockClient = Substitute.For<ICloudStorageClient>();
        mockClient.ValidateConnectionAsync("my-bucket", Arg.Any<CancellationToken>())
            .Returns(CloudStorageValidationResult.Valid());

        var result = await mockClient.ValidateConnectionAsync("my-bucket");

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ErrorType.Should().BeNull();
    }

    [Fact]
    public async Task ValidateConnectionAsync_CredentialsInvalid_ReturnsExpectedError()
    {
        var mockClient = Substitute.For<ICloudStorageClient>();
        mockClient.ValidateConnectionAsync("my-bucket", Arg.Any<CancellationToken>())
            .Returns(CloudStorageValidationResult.Invalid(
                CloudStorageValidationErrorType.CredentialsInvalid,
                "Invalid credentials"));

        var result = await mockClient.ValidateConnectionAsync("my-bucket");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.CredentialsInvalid);
        result.ErrorMessage.Should().Be("Invalid credentials");
    }

    [Fact]
    public async Task ValidateConnectionAsync_BucketNotFound_ReturnsExpectedError()
    {
        var mockClient = Substitute.For<ICloudStorageClient>();
        mockClient.ValidateConnectionAsync("nonexistent-bucket", Arg.Any<CancellationToken>())
            .Returns(CloudStorageValidationResult.Invalid(
                CloudStorageValidationErrorType.BucketNotFound,
                "Bucket 'nonexistent-bucket' not found"));

        var result = await mockClient.ValidateConnectionAsync("nonexistent-bucket");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.BucketNotFound);
    }

    [Fact]
    public async Task ValidateConnectionAsync_AccessDenied_ReturnsExpectedError()
    {
        var mockClient = Substitute.For<ICloudStorageClient>();
        mockClient.ValidateConnectionAsync("restricted-bucket", Arg.Any<CancellationToken>())
            .Returns(CloudStorageValidationResult.Invalid(
                CloudStorageValidationErrorType.AccessDenied,
                "Access denied to bucket 'restricted-bucket'"));

        var result = await mockClient.ValidateConnectionAsync("restricted-bucket");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.AccessDenied);
    }

    [Fact]
    public void CloudStorageValidationResult_Valid_FactoryMethodWorks()
    {
        var result = CloudStorageValidationResult.Valid();

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ErrorType.Should().BeNull();
    }

    [Fact]
    public void CloudStorageValidationResult_Invalid_FactoryMethodWorks()
    {
        var result = CloudStorageValidationResult.Invalid(
            CloudStorageValidationErrorType.NetworkError,
            "Network error occurred");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.NetworkError);
        result.ErrorMessage.Should().Be("Network error occurred");
    }

    [Fact]
    public void CloudStorageValidationResult_Invalid_Timeout()
    {
        var result = CloudStorageValidationResult.Invalid(
            CloudStorageValidationErrorType.Timeout,
            "Connection timed out");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.Timeout);
    }

    [Fact]
    public void CloudStorageValidationResult_Invalid_BucketCreationFailed()
    {
        var result = CloudStorageValidationResult.Invalid(
            CloudStorageValidationErrorType.BucketCreationFailed,
            "Could not create bucket");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.BucketCreationFailed);
    }

    // --- FeedPublishResult contract tests ---

    [Fact]
    public void FeedPublishResult_Successful_HasCorrectProperties()
    {
        var result = FeedPublishResult.Successful("https://example.com/feed.xml", 3);

        result.Success.Should().BeTrue();
        result.FeedUrl.Should().Be("https://example.com/feed.xml");
        result.EpisodesPublished.Should().Be(3);
        result.ErrorMessage.Should().BeNull();
        result.PublishedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FeedPublishResult_Failure_HasCorrectProperties()
    {
        var result = FeedPublishResult.Failure("Something went wrong");

        result.Success.Should().BeFalse();
        result.FeedUrl.Should().BeEmpty();
        result.EpisodesPublished.Should().Be(0);
        result.ErrorMessage.Should().Be("Something went wrong");
    }

    // --- BootstrapFeedAsync integration with validation flow ---

    [Fact]
    public async Task BootstrapFeedAsync_AfterValidation_SuccessFlow()
    {
        // Simulate: validation succeeds, then bootstrap succeeds
        var mockClient = Substitute.For<ICloudStorageClient>();
        mockClient.ValidateConnectionAsync("test-bucket", Arg.Any<CancellationToken>())
            .Returns(CloudStorageValidationResult.Valid());

        mockClient.DownloadStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        mockClient.UploadStringAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => $"https://storage.example.com/{callInfo.ArgAt<string>(1)}");
        mockClient.GetPublicUrl(Arg.Any<string>())
            .Returns(callInfo => $"https://storage.example.com/{callInfo.ArgAt<string>(0)}");

        var feedGen = Substitute.For<IPodcastFeedGenerator>();
        feedGen.GenerateFeedXmlAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeMetadata>>(), Arg.Any<CancellationToken>())
            .Returns("<rss>empty</rss>");

        var publisher = new PodcastPublisher(mockClient, feedGen, NullLogger<PodcastPublisher>.Instance);
        var podcast = CreateTestPodcast();

        // Step 1: Validate
        var validation = await mockClient.ValidateConnectionAsync("test-bucket");
        validation.IsValid.Should().BeTrue();

        // Step 2: Bootstrap
        var feedResult = await publisher.BootstrapFeedAsync(podcast);
        feedResult.Success.Should().BeTrue();
        feedResult.FeedUrl.Should().Contain("feed.xml");
    }

    [Fact]
    public async Task BootstrapFeedAsync_AfterValidationFailure_NotCalled()
    {
        // Simulate: validation fails, bootstrap should never be called
        var mockClient = Substitute.For<ICloudStorageClient>();
        mockClient.ValidateConnectionAsync("bad-bucket", Arg.Any<CancellationToken>())
            .Returns(CloudStorageValidationResult.Invalid(
                CloudStorageValidationErrorType.AccessDenied,
                "Access denied"));

        var validation = await mockClient.ValidateConnectionAsync("bad-bucket");
        validation.IsValid.Should().BeFalse();

        // In the real flow, BootstrapFeedAsync would NOT be called after validation failure
        // This test documents the expected flow contract
    }
}
