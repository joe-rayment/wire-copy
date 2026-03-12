// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TermReader.Application.DTOs;
using TermReader.Application.DTOs.Audio;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.DTOs.Browser;
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

[Trait("Category", "Unit")]
public class PodcastOrchestratorTests : IDisposable
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

    public PodcastOrchestratorTests()
    {
        // ReadingListContentProvider sub-dependencies
        _pageLoader = Substitute.For<IPageLoader>();
        _contentExtractor = Substitute.For<IReadableContentExtractor>();
        _preloadService = Substitute.For<IPreloadService>();
        _pageCache = Substitute.For<IPageCache>();
        _browserSession = Substitute.For<IBrowserSession>();
        _articleCache = Substitute.For<IArticleContentCache>();

        var browserConfig = Options.Create(new BrowserConfiguration { Headless = true });

        var contentProvider = new ReadingListContentProvider(
            _pageLoader,
            _contentExtractor,
            browserConfig,
            _preloadService,
            _pageCache,
            _browserSession,
            _articleCache,
            NullLogger<ReadingListContentProvider>.Instance);

        // Direct dependencies
        _ttsService = Substitute.For<ITtsService>();
        _audioCache = Substitute.For<ITtsAudioCache>();
        _audioAssembler = Substitute.For<IAudioAssembler>();
        _publisher = Substitute.For<IPodcastPublisher>();

        _tempDir = Path.Combine(Path.GetTempPath(), $"podcast-orch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var podcastConfig = Options.Create(new PodcastConfiguration
        {
            Title = "Test Podcast",
            Description = "Test podcast description",
            Author = "Test Author",
            Language = "en-us",
            Category = "Technology",
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

        // Default: TTS is configured, FFmpeg is available
        _ttsService.IsConfigured.Returns(true);
        _audioAssembler.ValidatePrerequisitesAsync(Arg.Any<CancellationToken>())
            .Returns(true);
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

    private static Collection CreateNamedCollection(string name, params string[] urls)
    {
        var collection = Collection.Create(name);
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

    private static ExtractedArticle CreateExtractedArticle(string url, string title = "Test Article", string? text = null)
    {
        var articleText = text ?? DefaultArticleText;
        return new ExtractedArticle
        {
            Title = title,
            CleanedText = articleText,
            Url = url,
            WordCount = articleText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
        };
    }

    private void SetupArticleCacheHits(params (string Url, ExtractedArticle Article)[] entries)
    {
        foreach (var (url, article) in entries)
        {
            _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
                .Returns(article);
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

    private void SetupTtsGeneration(byte[]? audioData = null, bool success = true, string? error = null)
    {
        var data = audioData ?? [1, 2, 3, 4, 5];
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(success
                ? TtsGenerationResult.Successful(data, 100, 1)
                : TtsGenerationResult.Failure(error ?? "TTS failed"));
    }

    private void SetupTtsAudioCacheMiss()
    {
        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);
    }

    private void SetupTtsAudioCacheHit(string audioFilePath = "/tmp/cached-audio.aac")
    {
        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TtsAudioCacheEntry
            {
                CacheKey = "test-key",
                AudioFilePath = audioFilePath,
                FileSizeBytes = 1024,
                CachedAtUtc = DateTime.UtcNow,
                ContentHash = "hash",
                TtsConfigHash = "config-hash",
            });
    }

    private void SetupTtsAudioCachePut(string audioFilePath = "/tmp/new-audio.aac")
    {
        _audioCache.PutAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<CancellationToken>())
            .Returns(new TtsAudioCacheEntry
            {
                CacheKey = "new-key",
                AudioFilePath = audioFilePath,
                FileSizeBytes = 512,
                CachedAtUtc = DateTime.UtcNow,
                ContentHash = "new-hash",
                TtsConfigHash = "config-hash",
            });
    }

    private void SetupAssemblySuccess(string? outputPath = null)
    {
        var path = outputPath ?? Path.Combine(_tempDir, "output.m4b");
        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .Returns(AssemblyResult.Successful(
                path,
                TimeSpan.FromMinutes(10),
                1024 * 1024,
                new List<AudioChapterMarker>
                {
                    new() { Title = "Chapter 1", StartTime = TimeSpan.Zero },
                }));
    }

    private void SetupPublishSuccess(string feedUrl = "https://storage.example.com/feed.xml")
    {
        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Successful(feedUrl, 1));
    }

    private void SetupPublishFailure(string error = "Upload failed")
    {
        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Failure(error));
    }

    /// <summary>
    /// Sets up a complete happy-path pipeline for one article.
    /// </summary>
    private void SetupHappyPath(string url = "https://example.com/article1")
    {
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();
    }

    #endregion

    #region GeneratePodcastAsync — Pre-flight Checks

    [Fact]
    public async Task GeneratePodcastAsync_NullCollection_ThrowsArgumentNullException()
    {
        Func<Task> act = () => _sut.GeneratePodcastAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GeneratePodcastAsync_TtsNotConfigured_ReturnsFailure()
    {
        _ttsService.IsConfigured.Returns(false);
        var collection = CreateCollection("https://example.com/a");

        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("TTS service is not configured");
    }

    [Fact]
    public async Task GeneratePodcastAsync_FfmpegMissing_ReturnsFailure()
    {
        _audioAssembler.ValidatePrerequisitesAsync(Arg.Any<CancellationToken>())
            .Returns(false);
        var collection = CreateCollection("https://example.com/a");

        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("FFmpeg");
    }

    [Fact]
    public async Task GeneratePodcastAsync_TtsNotConfigured_DoesNotCallAssembler()
    {
        _ttsService.IsConfigured.Returns(false);
        var collection = CreateCollection("https://example.com/a");

        await _sut.GeneratePodcastAsync(collection);

        await _audioAssembler.DidNotReceive().ValidatePrerequisitesAsync(Arg.Any<CancellationToken>());
    }

    #endregion

    #region GeneratePodcastAsync — Content Extraction

    [Fact]
    public async Task GeneratePodcastAsync_NoArticlesExtracted_ReturnsFailure()
    {
        // Empty collection items exist but article cache returns null (no content)
        var collection = CreateCollection("https://example.com/empty");
        _articleCache.TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(Arg.Any<string>()).Returns(false);
        _preloadService.WaitForInFlightAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No readable articles");
    }

    [Fact]
    public async Task GeneratePodcastAsync_EmptyCollection_ReturnsFailure()
    {
        var collection = Collection.Create("Empty");

        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No readable articles");
    }

    #endregion

    #region GeneratePodcastAsync — Budget Validation

    [Fact]
    public async Task GeneratePodcastAsync_CostExceedsBudget_ReturnsFailure()
    {
        var url = "https://example.com/expensive";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        // Cost exceeds the 5.00 budget
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 10.00m);

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds budget");
    }

    [Fact]
    public async Task GeneratePodcastAsync_CostExceedsBudget_DoesNotCallTts()
    {
        var url = "https://example.com/expensive";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 10.00m);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _ttsService.DidNotReceive().GenerateAudioAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IProgress<TtsProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePodcastAsync_CostExactlyAtBudget_Proceeds()
    {
        var url = "https://example.com/article";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 5.00m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
    }

    #endregion

    #region GeneratePodcastAsync — Happy Path

    [Fact]
    public async Task GeneratePodcastAsync_SingleArticle_ReturnsSuccess()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.ArticlesFailed.Should().Be(0);
        result.TotalDuration.Should().BeGreaterThan(TimeSpan.Zero);
        result.FileSizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GeneratePodcastAsync_SingleArticle_ReturnsFeedUrl()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.FeedUrl.Should().Be("https://storage.example.com/feed.xml");
    }

    [Fact]
    public async Task GeneratePodcastAsync_SingleArticle_ReturnsLocalFilePath()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.LocalFilePath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GeneratePodcastAsync_MultipleArticles_ProcessesAll()
    {
        var url1 = "https://example.com/article1";
        var url2 = "https://example.com/article2";
        var url3 = "https://example.com/article3";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Article 1")),
            (url2, CreateExtractedArticle(url2, "Article 2")),
            (url3, CreateExtractedArticle(url3, "Article 3")));
        SetupCacheAnalysis(cached: 0, uncached: 3, cost: 0.15m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url1, url2, url3);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(3);
    }

    [Fact]
    public async Task GeneratePodcastAsync_HappyPath_CallsAssemblerWithCorrectSegments()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _audioAssembler.Received(1).AssembleAsync(
            Arg.Is<AssemblyRequest>(r => r.Segments.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePodcastAsync_HappyPath_CallsPublisher()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _publisher.Received(1).PublishFeedAsync(
            Arg.Any<PodcastMetadata>(),
            Arg.Any<IReadOnlyList<EpisodeSource>>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region GeneratePodcastAsync — TTS Cache Hits

    [Fact]
    public async Task GeneratePodcastAsync_AllArticlesCached_DoesNotCallTtsService()
    {
        var url = "https://example.com/cached";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 1, uncached: 0, cost: 0m);
        SetupTtsAudioCacheHit();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _ttsService.DidNotReceive().GenerateAudioAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IProgress<TtsProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePodcastAsync_CacheHit_ReportsCachedCount()
    {
        var url = "https://example.com/cached";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 1, uncached: 0, cost: 0m);
        SetupTtsAudioCacheHit();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesCached.Should().Be(1);
    }

    [Fact]
    public async Task GeneratePodcastAsync_CacheMiss_StoresInCache()
    {
        var url = "https://example.com/fresh";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _audioCache.Received(1).PutAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region GeneratePodcastAsync — TTS Failures

    [Fact]
    public async Task GeneratePodcastAsync_SingleArticleTtsFails_ReturnsFailure()
    {
        var url = "https://example.com/fail";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration(success: false, error: "API error");

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("All articles failed TTS generation");
    }

    [Fact]
    public async Task GeneratePodcastAsync_PartialTtsFailure_StillSucceeds()
    {
        var url1 = "https://example.com/good";
        var url2 = "https://example.com/bad";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Good Article")),
            (url2, CreateExtractedArticle(url2, "Bad Article")));
        SetupCacheAnalysis(cached: 0, uncached: 2, cost: 0.10m);
        SetupTtsAudioCacheMiss();

        // Match by title parameter (2nd arg) which is unique per article
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Is("Good Article"),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Successful([1, 2, 3], 100, 1));

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Is("Bad Article"),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Failure("TTS API error"));

        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.ArticlesFailed.Should().Be(1);
    }

    [Fact]
    public async Task GeneratePodcastAsync_PartialTtsFailure_IncludesFailureDetails()
    {
        var url1 = "https://example.com/good";
        var url2 = "https://example.com/bad";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Good Article")),
            (url2, CreateExtractedArticle(url2, "Bad Article")));
        SetupCacheAnalysis(cached: 0, uncached: 2, cost: 0.10m);
        SetupTtsAudioCacheMiss();

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Is("Good Article"),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Successful([1, 2, 3], 100, 1));

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Is("Bad Article"),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(TtsGenerationResult.Failure("Quota exceeded"));

        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.FailedArticleDetails.Should().ContainSingle(f => f.Title == "Bad Article");
        result.FailedArticleDetails[0].Reason.Should().Contain("Quota exceeded");
    }

    [Fact]
    public async Task GeneratePodcastAsync_TtsReturnsNullAudioData_TreatsAsFailure()
    {
        var url = "https://example.com/null-audio";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(new TtsGenerationResult
            {
                Success = true,
                AudioData = null,
                CharactersProcessed = 100,
                ChunksCompleted = 1,
            });

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("All articles failed TTS generation");
    }

    #endregion

    #region GeneratePodcastAsync — Assembly Failure

    [Fact]
    public async Task GeneratePodcastAsync_AssemblyFails_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();

        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .Returns(AssemblyResult.Failure("FFmpeg crashed"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("M4B assembly failed");
        result.ErrorMessage.Should().Contain("FFmpeg crashed");
    }

    [Fact]
    public async Task GeneratePodcastAsync_AssemblyReturnsEmptyPath_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();

        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AssemblyResult
            {
                Success = true,
                OutputPath = string.Empty,
            });

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("M4B assembly failed");
    }

    #endregion

    #region GeneratePodcastAsync — Publishing

    [Fact]
    public async Task GeneratePodcastAsync_PublishFails_StillReturnsSuccess()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishFailure("Cloud storage unavailable");

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.FeedUrl.Should().BeNull();
        result.LocalFilePath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GeneratePodcastAsync_PublishSuccess_IncludesFeedUrl()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.FeedUrl.Should().Be("https://storage.example.com/feed.xml");
    }

    [Fact]
    public async Task GeneratePodcastAsync_PublishSuccess_IncludesCostInfo()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.12m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.TotalCost.Should().Be(0.12m);
    }

    #endregion

    #region GeneratePodcastAsync — Progress Reporting

    [Fact]
    public async Task GeneratePodcastAsync_ReportsProgressPhases()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var phases = new List<PodcastPhase>();
        var progress = new SynchronousProgress<PodcastProgress>(p => phases.Add(p.Phase));

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection, progress);

        phases.Should().Contain(PodcastPhase.CachingContent);
        phases.Should().Contain(PodcastPhase.GeneratingAudio);
        phases.Should().Contain(PodcastPhase.AssemblingAudio);
        phases.Should().Contain(PodcastPhase.Publishing);
    }

    #endregion

    #region GeneratePodcastAsync — Cancellation

    [Fact]
    public async Task GeneratePodcastAsync_CancelledBeforeTts_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GeneratePodcastAsync_CancelledDuringTts_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();

        using var cts = new CancellationTokenSource();

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(3).ThrowIfCancellationRequested();
                return TtsGenerationResult.Failure("Should not reach here");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region GeneratePodcastAsync — Exception Handling

    [Fact]
    public async Task GeneratePodcastAsync_UnexpectedExceptionDuringTts_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();

        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    [Fact]
    public async Task GeneratePodcastAsync_AssemblerThrowsException_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();

        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Disk full");
    }

    [Fact]
    public async Task GeneratePodcastAsync_PublisherThrowsException_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();

        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Connection refused");
    }

    #endregion

    #region GeneratePodcastAsync — Cache Reuse from AnalyzeCacheStatusAsync

    [Fact]
    public async Task GeneratePodcastAsync_ReusesArticlesFromAnalyze_WhenCollectionNameMatches()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);

        // First: analyze (this caches the articles)
        await _sut.AnalyzeCacheStatusAsync(collection);

        // Reset call count for article cache
        _articleCache.ClearReceivedCalls();

        // Second: generate (should reuse cached articles, not re-extract)
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();

        // Article content cache should NOT have been called again during generation
        await _articleCache.DidNotReceive().TryGetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePodcastAsync_DoesNotReuseCachedArticles_WhenCollectionNameDiffers()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var analyzeCollection = CreateNamedCollection("Analysis Collection", url);
        var generateCollection = CreateNamedCollection("Different Collection", url);

        // Analyze with one collection name
        await _sut.AnalyzeCacheStatusAsync(analyzeCollection);

        _articleCache.ClearReceivedCalls();

        // Generate with a different collection name — should re-extract
        var result = await _sut.GeneratePodcastAsync(generateCollection);

        result.Success.Should().BeTrue();

        // Article cache should have been queried again during extraction
        await _articleCache.Received().TryGetAsync(url, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePodcastAsync_ClearsCachedArticlesAfterUse()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);

        // Analyze then generate
        await _sut.AnalyzeCacheStatusAsync(collection);
        await _sut.GeneratePodcastAsync(collection);

        _articleCache.ClearReceivedCalls();

        // A second Generate call should re-extract (cache was cleared after first use)
        await _sut.GeneratePodcastAsync(collection);

        await _articleCache.Received().TryGetAsync(url, Arg.Any<CancellationToken>());
    }

    #endregion

    #region AnalyzeCacheStatusAsync

    [Fact]
    public async Task AnalyzeCacheStatusAsync_NullCollection_ThrowsArgumentNullException()
    {
        Func<Task> act = () => _sut.AnalyzeCacheStatusAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AnalyzeCacheStatusAsync_EmptyCollection_ReturnsEmptyAnalysis()
    {
        var collection = Collection.Create("Empty");

        var result = await _sut.AnalyzeCacheStatusAsync(collection);

        result.TotalArticles.Should().Be(0);
        result.CachedArticles.Should().Be(0);
        result.UncachedArticles.Should().Be(0);
        result.EstimatedCost.Should().Be(0);
        result.ArticleStatuses.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeCacheStatusAsync_WithArticles_ReturnsCacheAnalysis()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        var expectedAnalysis = new CacheAnalysis
        {
            TotalArticles = 1,
            CachedArticles = 1,
            UncachedArticles = 0,
            EstimatedCost = 0m,
            ArticleStatuses = [],
        };
        _audioCache.AnalyzeCollectionAsync(
                Arg.Any<IReadOnlyList<(string, string, string)>>(),
                Arg.Any<CancellationToken>())
            .Returns(expectedAnalysis);

        var collection = CreateCollection(url);
        var result = await _sut.AnalyzeCacheStatusAsync(collection);

        result.TotalArticles.Should().Be(1);
        result.CachedArticles.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeCacheStatusAsync_ExceptionDuringAnalysis_ReturnsEmptyAnalysis()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        _audioCache.AnalyzeCollectionAsync(
                Arg.Any<IReadOnlyList<(string, string, string)>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cache corrupted"));

        var collection = CreateCollection(url);
        var result = await _sut.AnalyzeCacheStatusAsync(collection);

        result.TotalArticles.Should().Be(0);
        result.EstimatedCost.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeCacheStatusAsync_CancellationToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var collection = CreateCollection("https://example.com/a");
        Func<Task> act = () => _sut.AnalyzeCacheStatusAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AnalyzeCacheStatusAsync_ReportsProgress()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 1, uncached: 0, cost: 0m);

        var progressReports = new List<ContentExtractionProgress>();
        var progress = new SynchronousProgress<ContentExtractionProgress>(p => progressReports.Add(p));

        var collection = CreateCollection(url);
        await _sut.AnalyzeCacheStatusAsync(collection, progress);

        progressReports.Should().NotBeEmpty();
    }

    #endregion

    #region GeneratePodcastAsync — Assembly Request Metadata

    [Fact]
    public async Task GeneratePodcastAsync_PassesPodcastConfigToAssembly()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _audioAssembler.Received(1).AssembleAsync(
            Arg.Is<AssemblyRequest>(r =>
                r.Metadata.Title.Contains("Test Podcast") &&
                r.Metadata.Author == "Test Author"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePodcastAsync_AssemblyOutputPathIncludesCollectionName()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _audioAssembler.Received(1).AssembleAsync(
            Arg.Is<AssemblyRequest>(r => r.OutputPath.Contains("Test Collection")),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region GeneratePodcastAsync — Podcast Metadata

    [Fact]
    public async Task GeneratePodcastAsync_PassesPodcastMetadataToPublisher()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _publisher.Received(1).PublishFeedAsync(
            Arg.Is<PodcastMetadata>(m =>
                m.Title == "Test Podcast" &&
                m.Author == "Test Author" &&
                m.Language == "en-us" &&
                m.Category == "Technology"),
            Arg.Any<IReadOnlyList<EpisodeSource>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeneratePodcastAsync_EpisodeTitleIncludesCollectionName()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _publisher.Received(1).PublishFeedAsync(
            Arg.Any<PodcastMetadata>(),
            Arg.Is<IReadOnlyList<EpisodeSource>>(e =>
                e.Count == 1 && e[0].Title.Contains("Test Collection")),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region GeneratePodcastAsync — Extraction Failures Propagation

    [Fact]
    public async Task GeneratePodcastAsync_ExtractionFailures_IncludedInSuccessResult()
    {
        // Set up two URLs: one succeeds extraction, one fails
        var goodUrl = "https://example.com/good";
        var badUrl = "https://example.com/bad";

        // Only the good URL returns content from article cache
        _articleCache.TryGetAsync(goodUrl, Arg.Any<CancellationToken>())
            .Returns(CreateExtractedArticle(goodUrl, "Good Article"));
        _articleCache.TryGetAsync(badUrl, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);

        // Bad URL: no page cache, no preload, no page loader result
        _pageCache.Contains(badUrl).Returns(false);
        _preloadService.WaitForInFlightAsync(badUrl, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, badUrl);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        // Extraction failures from the content provider should appear in FailedArticleDetails
        result.FailedArticleDetails.Should().Contain(f => f.Url == badUrl);
    }

    [Fact]
    public async Task GeneratePodcastAsync_CachedExtractionFailures_CarriedFromAnalyze()
    {
        // Set up: one good URL, one bad URL that fails extraction
        var goodUrl = "https://example.com/good";
        var badUrl = "https://example.com/bad";

        _articleCache.TryGetAsync(goodUrl, Arg.Any<CancellationToken>())
            .Returns(CreateExtractedArticle(goodUrl, "Good Article"));
        _articleCache.TryGetAsync(badUrl, Arg.Any<CancellationToken>())
            .Returns((ExtractedArticle?)null);
        _pageCache.Contains(badUrl).Returns(false);
        _preloadService.WaitForInFlightAsync(badUrl, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((PageLoadResult?)null);

        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, badUrl);

        // Analyze first (caches articles + failures)
        await _sut.AnalyzeCacheStatusAsync(collection);

        // Generate should reuse cached articles AND extraction failures
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.FailedArticleDetails.Should().Contain(f => f.Url == badUrl);
    }

    #endregion

    #region GeneratePodcastAsync — Progress Detail Verification

    [Fact]
    public async Task GeneratePodcastAsync_ProgressReportsCorrectArticleCounts()
    {
        var url1 = "https://example.com/article1";
        var url2 = "https://example.com/article2";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Article 1")),
            (url2, CreateExtractedArticle(url2, "Article 2")));
        SetupCacheAnalysis(cached: 0, uncached: 2, cost: 0.10m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var audioPhaseReports = new List<PodcastProgress>();
        var progress = new SynchronousProgress<PodcastProgress>(p =>
        {
            if (p.Phase == PodcastPhase.GeneratingAudio)
            {
                audioPhaseReports.Add(p);
            }
        });

        var collection = CreateCollection(url1, url2);
        await _sut.GeneratePodcastAsync(collection, progress);

        audioPhaseReports.Should().Contain(p => p.TotalArticles == 2);
        audioPhaseReports.Should().Contain(p => p.CurrentArticle == 1);
        audioPhaseReports.Should().Contain(p => p.CurrentArticle == 2);
    }

    [Fact]
    public async Task GeneratePodcastAsync_CachedArticle_ReportsIsFromCache()
    {
        var url = "https://example.com/cached";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 1, uncached: 0, cost: 0m);
        SetupTtsAudioCacheHit();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var cacheReports = new List<PodcastProgress>();
        var progress = new SynchronousProgress<PodcastProgress>(p =>
        {
            if (p.IsFromCache)
            {
                cacheReports.Add(p);
            }
        });

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection, progress);

        cacheReports.Should().NotBeEmpty();
        cacheReports.Should().AllSatisfy(p => p.Phase.Should().Be(PodcastPhase.GeneratingAudio));
    }

    [Fact]
    public async Task GeneratePodcastAsync_FinalProgressReport_Is100Percent()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        PodcastProgress? lastReport = null;
        var progress = new SynchronousProgress<PodcastProgress>(p => lastReport = p);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection, progress);

        lastReport.Should().NotBeNull();
        lastReport!.PercentComplete.Should().Be(100);
        lastReport.Phase.Should().Be(PodcastPhase.Publishing);
    }

    #endregion

    #region GeneratePodcastAsync — Content Quality Gate

    [Fact]
    public async Task GeneratePodcastAsync_ArticleBelowMinimumWordCount_IsSkipped()
    {
        var goodUrl = "https://example.com/good";
        var shortUrl = "https://example.com/short";

        var goodArticle = CreateExtractedArticle(goodUrl, "Good Article");
        var shortArticle = CreateExtractedArticle(shortUrl, "Short Article", "Only a few words.");

        SetupArticleCacheHits(
            (goodUrl, goodArticle),
            (shortUrl, shortArticle));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, shortUrl);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.FailedArticleDetails.Should().Contain(f =>
            f.Title == "Short Article" && f.Reason.Contains("too short"));
    }

    [Fact]
    public async Task GeneratePodcastAsync_ArticleWithEmptyTitle_IsSkipped()
    {
        var goodUrl = "https://example.com/good";
        var noTitleUrl = "https://example.com/no-title";

        var goodArticle = CreateExtractedArticle(goodUrl, "Good Article");
        var noTitleArticle = new ExtractedArticle
        {
            Title = string.Empty,
            CleanedText = DefaultArticleText,
            Url = noTitleUrl,
            WordCount = 120,
        };

        SetupArticleCacheHits(
            (goodUrl, goodArticle),
            (noTitleUrl, noTitleArticle));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, noTitleUrl);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.FailedArticleDetails.Should().Contain(f =>
            f.Reason.Contains("no title"));
    }

    [Fact]
    public async Task GeneratePodcastAsync_AllArticlesFailQualityCheck_ReturnsFailure()
    {
        var url1 = "https://example.com/short1";
        var url2 = "https://example.com/short2";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Short 1", "Too few words.")),
            (url2, CreateExtractedArticle(url2, "Short 2", "Also too short.")));

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("quality validation");
        result.FailedArticleDetails.Should().HaveCount(2);
    }

    [Fact]
    public async Task GeneratePodcastAsync_ArticleWithZeroWordCountProperty_FallsBackToTextCounting()
    {
        var url = "https://example.com/zero-wc";
        var article = new ExtractedArticle
        {
            Title = "Zero WordCount Property",
            CleanedText = DefaultArticleText,
            Url = url,
            WordCount = 0, // Property not set; should count from CleanedText
        };

        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
    }

    [Fact]
    public async Task GeneratePodcastAsync_ArticleExactlyAtMinimumWordCount_IsNotSkipped()
    {
        var url = "https://example.com/exact";
        // Build a string with exactly 100 words
        var words = Enumerable.Range(1, 100).Select(i => $"word{i}");
        var exactText = string.Join(" ", words);

        var article = CreateExtractedArticle(url, "Exact Minimum", exactText);

        SetupArticleCacheHits((url, article));
        SetupCacheAnalysis(cached: 0, uncached: 1, cost: 0.05m);
        SetupTtsAudioCacheMiss();
        SetupTtsGeneration();
        SetupTtsAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
    }

    [Fact]
    public async Task GeneratePodcastAsync_QualitySkippedArticle_DoesNotCallTts()
    {
        var url = "https://example.com/short";
        SetupArticleCacheHits(
            (url, CreateExtractedArticle(url, "Short Article", "Just three words.")));

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _ttsService.DidNotReceive().GenerateAudioAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IProgress<TtsProgress>>(),
            Arg.Any<CancellationToken>());
    }

    #endregion
}

/// <summary>
/// A synchronous IProgress implementation that invokes callbacks immediately
/// on the calling thread, avoiding race conditions with Progress&lt;T&gt;'s
/// thread pool posting in test environments without a SynchronizationContext.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
