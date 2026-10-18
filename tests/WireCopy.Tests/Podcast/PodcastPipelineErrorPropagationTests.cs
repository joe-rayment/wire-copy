// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WireCopy.Application.DTOs;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Tests error propagation through the full podcast pipeline and cancellation flows
/// across the narrator seam (<see cref="IPodcastNarrator"/>, workspace-c3ec4.2),
/// the publisher, and ReadingListContentProvider.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastPipelineErrorPropagationTests : IDisposable
{
    // Sub-dependencies of ReadingListContentProvider
    private readonly IPageLoader _pageLoader;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IPreloadService _preloadService;
    private readonly IPageCache _pageCache;
    private readonly IBrowserSession _browserSession;
    private readonly IArticleContentCache _articleCache;

    // Direct dependencies of PodcastOrchestrator
    private readonly IPodcastNarrator _narrator;
    private readonly IPodcastPublisher _publisher;

    private readonly PodcastOrchestrator _sut;
    private readonly string _tempDir;
    private readonly string _fakeAudioFile;

    public PodcastPipelineErrorPropagationTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _preloadService = Substitute.For<IPreloadService>();
        _pageCache = Substitute.For<IPageCache>();
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsBrowserAvailable.Returns(true);
        _articleCache = Substitute.For<IArticleContentCache>();

        var pageAccessQueue = Substitute.For<IPageAccessQueue>();
        pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new PageLease(Substitute.For<Microsoft.Playwright.IPage>(), () => { }));

        var contentProvider = new ReadingListContentProvider(
            _pageLoader,
            _contentExtractor,
            _preloadService,
            _pageCache,
            _browserSession,
            pageAccessQueue,
            _articleCache,
            NullLogger<ReadingListContentProvider>.Instance);

        _narrator = Substitute.For<IPodcastNarrator>();
        _publisher = Substitute.For<IPodcastPublisher>();

        _tempDir = Path.Combine(Path.GetTempPath(), $"pipeline-err-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fakeAudioFile = Path.Combine(_tempDir, "narrated.m4a");
        File.WriteAllBytes(_fakeAudioFile, [1, 2, 3, 4]);

        var podcastConfig = Options.Create(new PodcastConfiguration
        {
            Title = "Test Podcast",
            Description = "Test",
            Author = "Tester",
            Language = "en-us",
            Category = "Test",
            TempDirectory = _tempDir,
        });

        _sut = new PodcastOrchestrator(
            contentProvider,
            _narrator,
            _publisher,
            podcastConfig,
            NullLogger<PodcastOrchestrator>.Instance);

        // Default: pre-flight passes, narration succeeds.
        _narrator.IsConfigured.Returns(true);
        _narrator.EngineName.Returns("openai");
        _narrator.EstimateAsync(Arg.Any<NarrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NarrationEstimate { Success = true });
        SetupNarrateSuccess();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    #region Helpers

    private static Collection CreateCollection(params string[] urls)
    {
        var collection = Collection.Create("Test Collection");
        foreach (var url in urls)
        {
            collection.AddItem(url, $"Article: {url}");
        }

        return collection;
    }

    private const string DefaultArticleText =
        "This is a substantial article with enough words to pass the content quality validation gate. " +
        "It contains multiple sentences covering various topics to simulate real-world article content. " +
        "The quick brown fox jumps over the lazy dog near the riverbank on a sunny afternoon in spring. " +
        "Technology continues to evolve at a rapid pace bringing new innovations to every industry worldwide. " +
        "Researchers at the university published their findings in a peer-reviewed journal last week. " +
        "The economic outlook for the coming quarter remains cautiously optimistic according to analysts. " +
        "Several new policies were announced by the government aimed at improving public infrastructure. " +
        "Environmental organizations called for stronger regulations to protect endangered species and habitats. " +
        "The conference attracted hundreds of attendees from around the world eager to learn about advances. " +
        "In conclusion this article demonstrates that sufficient content length is important for audio generation.";

    private static ExtractedArticle CreateArticle(string url, string title = "Test Article")
    {
        return new ExtractedArticle
        {
            Title = title,
            CleanedText = DefaultArticleText,
            Url = url,
            WordCount = DefaultArticleText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
        };
    }

    private void SetupArticlesFromCache(params (string Url, string Title)[] articles)
    {
        foreach (var (url, title) in articles)
        {
            _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
                .Returns(CreateArticle(url, title));
        }
    }

    private NarrationResult SuccessfulNarration(IReadOnlyList<NarrationSkippedChapter>? skipped = null) => new()
    {
        Success = true,
        OutputPath = _fakeAudioFile,
        Bytes = 4,
        Duration = TimeSpan.FromMinutes(5),
        CostUsd = 0.05m,
        SkippedChapters = skipped ?? [],
    };

    private static NarrationResult FailedNarration(string message, NarrationErrorKind kind = NarrationErrorKind.Failed) => new()
    {
        Success = false,
        ErrorMessage = message,
        ErrorKind = kind,
    };

    private void SetupNarrateSuccess()
    {
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(SuccessfulNarration());
    }

    private void SetupNarrateFailure(string message, NarrationErrorKind kind = NarrationErrorKind.Failed)
    {
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(FailedNarration(message, kind));
    }

    private void SetupPublishSuccess()
    {
        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Successful("https://example.com/feed.xml", 1));
    }

    #endregion

    #region Full Pipeline — Failure at Each Stage

    // Guards:   when every article fails extraction the run fails with "No readable articles" and never
    //           reaches the narrator.
    // Fails if: the orchestrator sends an empty chapter list to NarrateAsync instead of failing early.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_ExtractionStageFailure_ReportsAllArticlesFailedDetails"
    [Fact]
    public async Task Pipeline_ExtractionStageFailure_ReportsAllArticlesFailedDetails()
    {
        var url1 = "https://example.com/a1";
        var url2 = "https://example.com/a2";

        // All extraction attempts fail
        _articleCache.TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(Arg.Any<string>()).Returns(false);
        _preloadService.WaitForInFlightAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);
        _pageLoader.LoadAsync(Arg.Any<PageLoadRequest>(), Arg.Any<CancellationToken>())
            .Returns(PageLoadResult.Failure("Connection timeout"));

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No readable articles");
        await _narrator.DidNotReceive().NarrateAsync(
            Arg.Any<NarrationRequest>(), Arg.Any<IProgress<NarrationProgress>?>(), Arg.Any<CancellationToken>());
    }

    // Guards:   a failed NarrationResult turns into a failed PodcastResult carrying the narrator's message
    //           verbatim, and nothing is published.
    // Fails if: the orchestrator ignores NarrationResult.Success, rewrites the message, or publishes anyway.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_NarrationFailure_ReportsNarratorMessage"
    [Fact]
    public async Task Pipeline_NarrationFailure_ReportsNarratorMessage()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupNarrateFailure("audio-engine: OpenAI API rate limited");

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("rate limited");
        await _publisher.DidNotReceive().PublishFeedAsync(
            Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
            Arg.Any<IProgress<PublishProgress>?>(), Arg.Any<CancellationToken>());
    }

    // Guards:   when narration fails after some articles already failed extraction, the result keeps those
    //           extraction failures in FailedArticleDetails alongside the narrator's error message.
    // Fails if: the narration-failure branch drops the extraction failures collected earlier.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_NarrationFailure_ReportsWithAllPriorExtractionFailures"
    [Fact]
    public async Task Pipeline_NarrationFailure_ReportsWithAllPriorExtractionFailures()
    {
        var url1 = "https://example.com/good";
        var url2 = "https://example.com/bad";

        // One article extracted, one fails extraction
        _articleCache.TryGetAsync(url1, Arg.Any<CancellationToken>())
            .Returns(CreateArticle(url1, "Good"));
        _articleCache.TryGetAsync(url2, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(url2).Returns(false);
        _preloadService.WaitForInFlightAsync(url2, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        SetupNarrateFailure("audio-engine: assembly failed: FFmpeg exited with code 1");

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("assembly failed");
        // Extraction failure for url2 should be in failure details
        result.FailedArticleDetails.Should().Contain(f => f.Url == url2);
    }

    // Guards:   a publish failure after a successful narration is graceful: the run still succeeds with a
    //           local file path and no feed URL.
    // Fails if: the orchestrator turns FeedPublishResult.Failure into a failed PodcastResult.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_PublishStageFailure_StillSucceeds_WithLocalPath"
    [Fact]
    public async Task Pipeline_PublishStageFailure_StillSucceeds_WithLocalPath()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));

        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Failure("GCS bucket not configured"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        // Publishing failure is graceful — still considered success
        result.Success.Should().BeTrue();
        result.FeedUrl.Should().BeNull();
        result.LocalFilePath.Should().NotBeNullOrEmpty();
    }

    // Guards:   extraction failures and narrator-skipped chapters are merged into one FailedArticleDetails
    //           list, with the skipped chapter mapped back to its article URL by chapter index.
    // Fails if: SkippedChapters are ignored, counted into ArticlesProcessed, or mapped to the wrong URL
    //           (the extraction failure is not a chapter, so chapter index 1 is the third article).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_CombinedExtractionAndSkippedChapterFailures_MergedInResult"
    [Fact]
    public async Task Pipeline_CombinedExtractionAndSkippedChapterFailures_MergedInResult()
    {
        var goodUrl = "https://example.com/good";
        var extractFailUrl = "https://example.com/extract-fail";
        var skippedUrl = "https://example.com/skipped";

        // Good article extracts and narrates fine
        _articleCache.TryGetAsync(goodUrl, Arg.Any<CancellationToken>())
            .Returns(CreateArticle(goodUrl, "Good Article"));

        // This article fails extraction
        _articleCache.TryGetAsync(extractFailUrl, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(extractFailUrl).Returns(false);
        _preloadService.WaitForInFlightAsync(extractFailUrl, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        // This article extracts but audio-engine skips its chapter
        _articleCache.TryGetAsync(skippedUrl, Arg.Any<CancellationToken>())
            .Returns(CreateArticle(skippedUrl, "Skipped Article"));

        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(SuccessfulNarration([new NarrationSkippedChapter(1, "Skipped Article", "Insufficient credits")]));
        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, extractFailUrl, skippedUrl);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.ArticlesFailed.Should().Be(2);

        // Both failure types should be in the details
        result.FailedArticleDetails.Should().Contain(f => f.Url == extractFailUrl);
        result.FailedArticleDetails.Should().Contain(f => f.Url == skippedUrl && f.Reason.Contains("Insufficient credits"));
        result.FailedArticleDetails.Should().HaveCount(2);
    }

    // Guards:   an exception thrown by the narrator (not a failed result) is caught and surfaces as a failed
    //           PodcastResult whose message contains the exception text, instead of crashing the caller.
    // Fails if: the orchestrator's catch block stops wrapping narrator exceptions or drops their message.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_NarratorThrows_ReturnsFailureWithMessage"
    [Fact]
    public async Task Pipeline_NarratorThrows_ReturnsFailureWithMessage()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));

        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("audio-engine socket closed"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("socket closed");
    }

    #endregion

    #region Cancellation During Narration and Publishing

    // Guards:   cancellation while the narrator is running propagates out of GeneratePodcastAsync as
    //           OperationCanceledException (the UI treats it as "cancelled", not as a failed run).
    // Fails if: the orchestrator's generic catch swallows OperationCanceledException into a failed result.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_CancellationDuringNarration_ThrowsOperationCanceledException"
    [Fact]
    public async Task Pipeline_CancellationDuringNarration_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));

        using var cts = new CancellationTokenSource();

        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return FailedNarration("unreachable");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // Guards:   cancellation while publishing propagates out of GeneratePodcastAsync as
    //           OperationCanceledException.
    // Fails if: the publish step catches the cancellation and reports a "publish failed but succeeded" result.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_CancellationDuringPublish_ThrowsOperationCanceledException"
    [Fact]
    public async Task Pipeline_CancellationDuringPublish_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));

        using var cts = new CancellationTokenSource();

        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(3).ThrowIfCancellationRequested();
                return FeedPublishResult.Failure("unreachable");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Empty Collection Handling

    // Guards:   an empty collection fails immediately with "No readable articles" and neither the narrator
    //           nor the publisher is called.
    // Fails if: the orchestrator sends an empty NarrationRequest or publishes an empty episode.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_EmptyCollection_ReturnsFailureImmediately"
    [Fact]
    public async Task Pipeline_EmptyCollection_ReturnsFailureImmediately()
    {
        var collection = Collection.Create("Empty");

        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No readable articles");

        // Should not have reached narration or publishing
        await _narrator.DidNotReceive().NarrateAsync(
            Arg.Any<NarrationRequest>(),
            Arg.Any<IProgress<NarrationProgress>?>(),
            Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().PublishFeedAsync(
            Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
            Arg.Any<IProgress<PublishProgress>?>(),
            Arg.Any<CancellationToken>());
    }

    // Guards:   analyzing an empty collection returns zero counts and never asks the narrator for an estimate.
    // Fails if: AnalyzeCacheStatusAsync calls EstimateAsync with no chapters.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.AnalyzeCacheStatus_EmptyCollection_ReturnsZeroCounts"
    [Fact]
    public async Task AnalyzeCacheStatus_EmptyCollection_ReturnsZeroCounts()
    {
        var collection = Collection.Create("Empty");

        var result = await _sut.AnalyzeCacheStatusAsync(collection);

        result.TotalArticles.Should().Be(0);
        result.CachedArticles.Should().Be(0);
        result.UncachedArticles.Should().Be(0);
        result.EstimatedCost.Should().Be(0);

        // Should not have asked the narrator for an estimate
        await _narrator.DidNotReceive().EstimateAsync(
            Arg.Any<NarrationRequest>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Concurrent Generation

    // Guards:   two concurrent GeneratePodcastAsync calls on one orchestrator both run to completion without
    //           throwing, and at least one succeeds.
    // Fails if: the orchestrator holds per-run state in fields that the second run corrupts.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastPipelineErrorPropagationTests.Pipeline_ConcurrentGenerateCalls_BothComplete"
    [Fact]
    public async Task Pipeline_ConcurrentGenerateCalls_BothComplete()
    {
        // Two concurrent pipeline runs with different collections
        var url1 = "https://example.com/a1";
        var url2 = "https://example.com/a2";

        _articleCache.TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateArticle(callInfo.ArgAt<string>(0)));

        SetupPublishSuccess();

        var collection1 = CreateCollection(url1);
        collection1.Rename("Collection A");
        var collection2 = CreateCollection(url2);
        collection2.Rename("Collection B");

        var task1 = _sut.GeneratePodcastAsync(collection1);
        var task2 = _sut.GeneratePodcastAsync(collection2);

        var results = await Task.WhenAll(task1, task2);

        // At least one should succeed (depends on internal state sharing)
        results.Should().Contain(r => r.Success);
    }

    #endregion
}
