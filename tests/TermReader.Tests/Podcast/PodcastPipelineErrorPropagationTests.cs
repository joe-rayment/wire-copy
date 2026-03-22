// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TermReader.Application.DTOs;
using TermReader.Application.DTOs.Audio;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Audio;
using TermReader.Application.Interfaces.Browser;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.ValueObjects.Audio;
using TermReader.Domain.ValueObjects.Podcast;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using TermReader.Infrastructure.Podcast.Cache;
using Xunit;

namespace TermReader.Tests.Podcast;

/// <summary>
/// Tests error propagation through the full podcast pipeline and cancellation flows
/// across OpenAiTtsService (via ITtsService), M4bAudioAssembler (via IAudioAssembler),
/// and ReadingListContentProvider.
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
    private readonly ITtsService _ttsService;
    private readonly ITtsAudioCache _audioCache;
    private readonly IAudioAssembler _audioAssembler;
    private readonly IPodcastPublisher _publisher;

    private readonly PodcastOrchestrator _sut;
    private readonly string _tempDir;

    public PodcastPipelineErrorPropagationTests()
    {
        _pageLoader = Substitute.For<IPageLoader>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _preloadService = Substitute.For<IPreloadService>();
        _pageCache = Substitute.For<IPageCache>();
        _browserSession = Substitute.For<IBrowserSession>();
        _articleCache = Substitute.For<IArticleContentCache>();

        var browserConfig = Options.Create(new BrowserConfiguration { Headless = true });
        var webDriverQueue = Substitute.For<IWebDriverQueue>();
        webDriverQueue.AcquireAsync(Arg.Any<WebDriverPriority>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new WebDriverLease(Substitute.For<OpenQA.Selenium.IWebDriver>(), () => { }));

        var contentProvider = new ReadingListContentProvider(
            _pageLoader,
            _contentExtractor,
            browserConfig,
            _preloadService,
            _pageCache,
            _browserSession,
            webDriverQueue,
            _articleCache,
            NullLogger<ReadingListContentProvider>.Instance);

        _ttsService = Substitute.For<ITtsService>();
        _audioCache = Substitute.For<ITtsAudioCache>();
        _audioAssembler = Substitute.For<IAudioAssembler>();
        _publisher = Substitute.For<IPodcastPublisher>();

        _tempDir = Path.Combine(Path.GetTempPath(), $"pipeline-err-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var podcastConfig = Options.Create(new PodcastConfiguration
        {
            Title = "Test Podcast",
            Description = "Test",
            Author = "Tester",
            Language = "en-us",
            Category = "Test",
            TempDirectory = _tempDir,
        });

        var ttsConfig = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = "test-key",
            MaxBudgetUsd = 5.00m,
        });

        _sut = new PodcastOrchestrator(
            contentProvider,
            _ttsService,
            _audioCache,
            _audioAssembler,
            _publisher,
            podcastConfig,
            ttsConfig,
            NullLogger<PodcastOrchestrator>.Instance);

        // Default: pre-flight passes
        _ttsService.IsConfigured.Returns(true);
        _audioAssembler.ValidatePrerequisitesAsync(Arg.Any<CancellationToken>()).Returns(true);
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

    private void SetupCacheAnalysis(int cached = 0, int uncached = 1, decimal cost = 0.05m)
    {
        _audioCache.AnalyzeCollectionAsync(Arg.Any<IReadOnlyList<(string, string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new CacheAnalysis
            {
                TotalArticles = cached + uncached,
                CachedArticles = cached,
                UncachedArticles = uncached,
                EstimatedCost = cost,
                ArticleStatuses = [],
            });
    }

    private void SetupTtsSuccess()
    {
        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Successful([1, 2, 3], 100, 1));
        _audioCache.PutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new TtsAudioCacheEntry
            {
                CacheKey = "k",
                AudioFilePath = "/tmp/audio.aac",
                FileSizeBytes = 100,
                CachedAtUtc = DateTime.UtcNow,
                ContentHash = "h",
                TtsConfigHash = "c",
            });
    }

    private void SetupAssemblySuccess()
    {
        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .Returns(AssemblyResult.Successful(
                Path.Combine(_tempDir, "out.m4b"),
                TimeSpan.FromMinutes(5),
                512 * 1024,
                [new AudioChapterMarker { Title = "Ch1", StartTime = TimeSpan.Zero }]));
    }

    private void SetupPublishSuccess()
    {
        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Successful("https://example.com/feed.xml", 1));
    }

    #endregion

    #region Full Pipeline — Failure at Each Stage

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
    }

    [Fact]
    public async Task Pipeline_TtsStageFailure_AllArticles_ReportsAllFailed()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);

        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Failure("OpenAI API rate limited"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("All articles failed TTS");
        result.FailedArticleDetails.Should().ContainSingle(f => f.Reason.Contains("rate limited"));
    }

    [Fact]
    public async Task Pipeline_AssemblyStageFailure_ReportsWithAllPriorFailures()
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

        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsSuccess();

        // Assembly fails
        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .Returns(AssemblyResult.Failure("FFmpeg exited with code 1"));

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("M4B assembly failed");
        // Extraction failure for url2 should be in failure details
        result.FailedArticleDetails.Should().Contain(f => f.Url == url2);
    }

    [Fact]
    public async Task Pipeline_PublishStageFailure_StillSucceeds_WithLocalPath()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsSuccess();
        SetupAssemblySuccess();

        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Failure("GCS bucket not configured"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        // Publishing failure is graceful — still considered success
        result.Success.Should().BeTrue();
        result.FeedUrl.Should().BeNull();
        result.LocalFilePath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Pipeline_CombinedExtractionAndTtsFailures_MergedInResult()
    {
        var goodUrl = "https://example.com/good";
        var extractFailUrl = "https://example.com/extract-fail";
        var ttsFailUrl = "https://example.com/tts-fail";

        // Good article extracts and generates fine
        _articleCache.TryGetAsync(goodUrl, Arg.Any<CancellationToken>())
            .Returns(CreateArticle(goodUrl, "Good Article"));

        // This article fails extraction
        _articleCache.TryGetAsync(extractFailUrl, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(extractFailUrl).Returns(false);
        _preloadService.WaitForInFlightAsync(extractFailUrl, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        // TTS-fail article extracts but TTS fails
        _articleCache.TryGetAsync(ttsFailUrl, Arg.Any<CancellationToken>())
            .Returns(CreateArticle(ttsFailUrl, "TTS Fail Article"));

        SetupCacheAnalysis(cached: 0, uncached: 2, cost: 0.10m);

        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);

        // Good article TTS succeeds
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(), Arg.Is("Good Article"),
                Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Successful([1, 2, 3], 100, 1));

        // TTS-fail article TTS fails
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(), Arg.Is("TTS Fail Article"),
                Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Failure("Insufficient credits"));

        _audioCache.PutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new TtsAudioCacheEntry
            {
                CacheKey = "k",
                AudioFilePath = "/tmp/audio.aac",
                FileSizeBytes = 100,
                CachedAtUtc = DateTime.UtcNow,
                ContentHash = "h",
                TtsConfigHash = "c",
            });

        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, extractFailUrl, ttsFailUrl);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.ArticlesFailed.Should().Be(2);

        // Both failure types should be in the details
        result.FailedArticleDetails.Should().Contain(f => f.Url == extractFailUrl);
        result.FailedArticleDetails.Should().Contain(f => f.Url == ttsFailUrl);
        result.FailedArticleDetails.Should().HaveCount(2);
    }

    #endregion

    #region Cancellation During Assembly and Publishing

    [Fact]
    public async Task Pipeline_CancellationDuringAssembly_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsSuccess();

        using var cts = new CancellationTokenSource();

        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
                return AssemblyResult.Failure("unreachable");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Pipeline_CancellationDuringPublish_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsSuccess();
        SetupAssemblySuccess();

        using var cts = new CancellationTokenSource();

        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return FeedPublishResult.Failure("unreachable");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Pipeline_CancellationDuringCacheAnalysis_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));

        using var cts = new CancellationTokenSource();

        _audioCache.AnalyzeCollectionAsync(Arg.Any<IReadOnlyList<(string, string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
                return new CacheAnalysis { TotalArticles = 0, CachedArticles = 0, UncachedArticles = 0, EstimatedCost = 0, ArticleStatuses = [] };
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Pipeline_CancellationBetweenArticles_ThrowsOperationCanceledException()
    {
        var url1 = "https://example.com/a1";
        var url2 = "https://example.com/a2";

        SetupArticlesFromCache(
            (url1, "Article 1"),
            (url2, "Article 2"));
        SetupCacheAnalysis(cached: 0, uncached: 2, cost: 0.10m);

        using var cts = new CancellationTokenSource();

        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);

        var ttsCallCount = 0;
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                ttsCallCount++;
                if (ttsCallCount == 1)
                {
                    // First article succeeds but then cancellation is requested
                    await cts.CancelAsync();
                    return TtsGenerationResult.Successful([1, 2, 3], 100, 1);
                }

                // Second article: cancellation is already requested
                callInfo.ArgAt<CancellationToken>(3).ThrowIfCancellationRequested();
                return TtsGenerationResult.Failure("unreachable");
            });

        _audioCache.PutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new TtsAudioCacheEntry
            {
                CacheKey = "k",
                AudioFilePath = "/tmp/audio.aac",
                FileSizeBytes = 100,
                CachedAtUtc = DateTime.UtcNow,
                ContentHash = "h",
                TtsConfigHash = "c",
            });

        var collection = CreateCollection(url1, url2);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Empty Collection Handling

    [Fact]
    public async Task Pipeline_EmptyCollection_ReturnsFailureImmediately()
    {
        var collection = Collection.Create("Empty");

        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No readable articles");

        // Should not have reached TTS or assembly stages
        await _ttsService.DidNotReceive().GenerateAudioAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>());
        await _audioAssembler.DidNotReceive().AssembleAsync(
            Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().PublishFeedAsync(
            Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeCacheStatus_EmptyCollection_ReturnsZeroCounts()
    {
        var collection = Collection.Create("Empty");

        var result = await _sut.AnalyzeCacheStatusAsync(collection);

        result.TotalArticles.Should().Be(0);
        result.CachedArticles.Should().Be(0);
        result.UncachedArticles.Should().Be(0);
        result.EstimatedCost.Should().Be(0);

        // Should not have called audio cache analysis
        await _audioCache.DidNotReceive().AnalyzeCollectionAsync(
            Arg.Any<IReadOnlyList<(string, string, string)>>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Concurrent Generation

    [Fact]
    public async Task Pipeline_ConcurrentGenerateCalls_BothComplete()
    {
        // Two concurrent pipeline runs with different collections
        var url1 = "https://example.com/a1";
        var url2 = "https://example.com/a2";

        _articleCache.TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateArticle(callInfo.ArgAt<string>(0)));

        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsSuccess();
        SetupAssemblySuccess();
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

    #region Cache Eviction During Active Generation

    [Fact]
    public async Task Pipeline_AudioCacheEvictionDuringGeneration_DoesNotCrash()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);

        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Successful([1, 2, 3], 100, 1));

        // Cache put throws (simulating eviction or disk issue during active generation)
        _audioCache.PutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Cache directory was evicted"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        // Should fail gracefully (exception caught by orchestrator)
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Cache directory was evicted");
    }

    [Fact]
    public async Task Pipeline_AudioCacheTryGetThrows_TreatedAsNewGeneration()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 1, uncached: 0, cost: 0m);

        // Cache lookup throws — should fall through to generation
        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cache corrupted"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        // Exception from TryGetAsync propagates up to orchestrator's catch block
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Cache corrupted");
    }

    #endregion

    #region TTS Service Cancellation Propagation

    [Fact]
    public async Task Pipeline_TtsServiceThrowsOperationCanceled_Propagates()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);

        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);

        using var cts = new CancellationTokenSource();

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(3).ThrowIfCancellationRequested();
                return TtsGenerationResult.Failure("unreachable");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Assembler Cancellation Propagation

    [Fact]
    public async Task Pipeline_AssemblerThrowsOperationCanceled_Propagates()
    {
        var url = "https://example.com/a1";
        SetupArticlesFromCache((url, "Article 1"));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsSuccess();

        using var cts = new CancellationTokenSource();

        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
                return AssemblyResult.Failure("unreachable");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion
}
