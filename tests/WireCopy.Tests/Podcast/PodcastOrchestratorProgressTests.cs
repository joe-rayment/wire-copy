// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs;
using WireCopy.Application.DTOs.Audio;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Audio;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.ValueObjects.Audio;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Tests for workspace-74zy (Phase A: surface real progress signals). Asserts the
/// orchestrator forwards <see cref="TtsProgress"/> chunk events,
/// <see cref="AssemblyProgress"/> per-segment + FFmpeg-percent events, and
/// <see cref="PublishProgress"/> per-episode upload events through the
/// <see cref="PodcastProgress"/> sink. Also covers the cache-hit count emission
/// at the start of the audio phase and monotonic <c>PhaseElapsed</c>.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastOrchestratorProgressTests : IDisposable
{
    private readonly IArticleContentCache _articleCache;
    private readonly ITtsService _ttsService;
    private readonly ITtsAudioCache _audioCache;
    private readonly IAudioAssembler _audioAssembler;
    private readonly IPodcastPublisher _publisher;
    private readonly PodcastOrchestrator _sut;
    private readonly string _tempDir;

    public PodcastOrchestratorProgressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"podcast-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var pageLoader = Substitute.For<IPageLoader>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();
        var preloadService = Substitute.For<IPreloadService>();
        var pageCache = Substitute.For<IPageCache>();
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        _articleCache = Substitute.For<IArticleContentCache>();

        var pageAccessQueue = Substitute.For<IPageAccessQueue>();
        pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new PageLease(Substitute.For<Microsoft.Playwright.IPage>(), () => { }));

        var contentProvider = new ReadingListContentProvider(
            pageLoader,
            contentExtractor,
            preloadService,
            pageCache,
            browserSession,
            pageAccessQueue,
            _articleCache,
            NullLogger<ReadingListContentProvider>.Instance);

        _ttsService = Substitute.For<ITtsService>();
        _audioCache = Substitute.For<ITtsAudioCache>();
        _audioAssembler = Substitute.For<IAudioAssembler>();
        _publisher = Substitute.For<IPodcastPublisher>();

        var podcastConfig = Options.Create(new PodcastConfiguration
        {
            Title = "Test Podcast",
            Author = "Test Author",
            Description = "desc",
            Language = "en-us",
            Category = "Technology",
            TempDirectory = _tempDir,
            MinimumWordCount = 5,
        });

        var ttsConfig = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = "test-key",
            MaxBudgetUsd = 5.00m,
        });

        _ttsService.IsConfigured.Returns(true);
        _audioAssembler.ValidatePrerequisitesAsync(Arg.Any<CancellationToken>()).Returns(true);

        _sut = new PodcastOrchestrator(
            contentProvider,
            _ttsService,
            _audioCache,
            _audioAssembler,
            _publisher,
            podcastConfig,
            ttsConfig,
            NullLogger<PodcastOrchestrator>.Instance);
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
            // best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GeneratePodcast_ForwardsTtsChunkProgress_IntoPodcastProgress()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupCacheAnalysis(cached: 0, uncached: 1);
        SetupAudioCacheMiss();
        SetupAudioCachePut();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        // TTS mock simulates two chunk progress reports.
        _ttsService.GenerateAudioAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TtsProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sink = callInfo.ArgAt<IProgress<TtsProgress>>(2);
                sink?.Report(new TtsProgress
                {
                    CurrentChunk = 1,
                    TotalChunks = 2,
                    CharactersProcessed = 100,
                    TotalCharacters = 200,
                    Message = "Chunk 1/2",
                });
                sink?.Report(new TtsProgress
                {
                    CurrentChunk = 2,
                    TotalChunks = 2,
                    CharactersProcessed = 200,
                    TotalCharacters = 200,
                    Message = "Chunk 2/2",
                });
                return Task.FromResult(TtsGenerationResult.Successful([1, 2, 3], 200, 2));
            });

        var events = new List<PodcastProgress>();
        var progress = new Progress<PodcastProgress>(p => events.Add(p));

        var result = await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        result.Success.Should().BeTrue();
        var chunkEvents = events.Where(e => e.CurrentArticleChunkTotal > 0).ToList();
        chunkEvents.Should().HaveCountGreaterThanOrEqualTo(2);
        chunkEvents.Should().Contain(e => e.CurrentArticleChunkIndex == 1 && e.CurrentArticleChunkTotal == 2);
        chunkEvents.Should().Contain(e => e.CurrentArticleChunkIndex == 2 && e.CurrentArticleChunkTotal == 2);
        chunkEvents.Last().CurrentArticleChunkPercent.Should().BeApproximately(100.0, 0.01);
    }

    [Fact]
    public async Task GeneratePodcast_EmitsCacheHitCounts_AtStartOfAudioPhase()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupCacheAnalysis(cached: 3, uncached: 2);
        SetupAudioCacheHit();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var events = new List<PodcastProgress>();
        var progress = new Progress<PodcastProgress>(p => events.Add(p));

        await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        var audio = events.Where(p => p.Phase == PodcastPhase.GeneratingAudio).ToList();
        audio.Should().NotBeEmpty();
        audio.Should().Contain(p => p.CachedArticleCount == 3 && p.UncachedArticleCount == 2,
            "weighted-ETA needs the orchestrator to surface the cache split");
    }

    [Fact]
    public async Task GeneratePodcast_ForwardsAssemblyProgress_AsAssemblingAudioPhase()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupCacheAnalysis(cached: 0, uncached: 1);
        SetupAudioCacheHit();
        SetupPublishSuccess();

        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<IProgress<AssemblyProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sink = callInfo.ArgAt<IProgress<AssemblyProgress>?>(1);
                sink?.Report(new AssemblyProgress { TotalSegments = 3, CompletedSegments = 1, Message = "Probed 1 of 3" });
                sink?.Report(new AssemblyProgress { TotalSegments = 3, CompletedSegments = 3, FfmpegPercent = 100, Message = "Concatenating (100%)" });
                return Task.FromResult(AssemblyResult.Successful(
                    Path.Combine(_tempDir, "out.m4b"),
                    TimeSpan.FromMinutes(10),
                    1024,
                    new List<AudioChapterMarker>()));
            });

        var events = new List<PodcastProgress>();
        var progress = new Progress<PodcastProgress>(p => events.Add(p));

        await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        var assembly = events.Where(p => p.Phase == PodcastPhase.AssemblingAudio).ToList();
        assembly.Should().HaveCountGreaterThanOrEqualTo(2);
        assembly.Should().Contain(p => p.AssembledSegments == 1 && p.AssembledSegmentsTotal == 3);
        assembly.Should().Contain(p => p.AssembledSegments == 3 && p.AssembledSegmentsTotal == 3);
    }

    [Fact]
    public async Task GeneratePodcast_ForwardsPublishProgress_AsPublishingPhase()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupCacheAnalysis(cached: 0, uncached: 1);
        SetupAudioCacheHit();
        SetupAssemblySuccess();

        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sink = callInfo.ArgAt<IProgress<PublishProgress>?>(2);
                sink?.Report(new PublishProgress
                {
                    TotalEpisodes = 1,
                    UploadedEpisodes = 0,
                    UploadedBytes = 512,
                    UploadedBytesTotal = 1024,
                });
                sink?.Report(new PublishProgress
                {
                    TotalEpisodes = 1,
                    UploadedEpisodes = 1,
                    UploadedBytes = 1024,
                    UploadedBytesTotal = 1024,
                });
                return Task.FromResult(FeedPublishResult.Successful("https://example.com/feed.xml", 1));
            });

        var events = new List<PodcastProgress>();
        var progress = new Progress<PodcastProgress>(p => events.Add(p));

        await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        var publish = events.Where(p => p.Phase == PodcastPhase.Publishing).ToList();
        publish.Should().Contain(p => p.UploadedBytes == 512 && p.UploadedBytesTotal == 1024);
        publish.Should().Contain(p => p.UploadedEpisodes == 1 && p.UploadedEpisodesTotal == 1);
    }

    [Fact]
    public async Task GeneratePodcast_PhaseElapsed_IsMonotonicWithinAudioPhase()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupCacheAnalysis(cached: 0, uncached: 1);
        SetupAudioCacheHit();
        SetupAssemblySuccess();
        SetupPublishSuccess();

        var events = new List<PodcastProgress>();
        var progress = new Progress<PodcastProgress>(p => events.Add(p));

        await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        var audioElapsed = events.Where(p => p.Phase == PodcastPhase.GeneratingAudio).Select(p => p.PhaseElapsed).ToList();
        audioElapsed.Should().NotBeEmpty();
        for (var i = 1; i < audioElapsed.Count; i++)
        {
            audioElapsed[i].Should().BeGreaterThanOrEqualTo(audioElapsed[i - 1],
                "PhaseElapsed reads a single per-phase Stopwatch so it must be monotonic across events in the same phase");
        }
    }

    private static Collection CreateCollection(string url)
    {
        var collection = Collection.Create("Progress Test Collection");
        collection.AddItem(url, $"Article: {url}");
        return collection;
    }

    private void SetupArticleCacheHit(string url, string title)
    {
        // ReadingListContentProvider sees a cache hit through IArticleContentCache.
        // To bypass actual page fetches in this unit test we make the cache return
        // a pre-extracted article. NSubstitute's For<IArticleContentCache>() returns
        // null for unmocked Tasks, so we wire enough of the surface to keep the
        // orchestrator on the happy path.
        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(new ExtractedArticle
            {
                Url = url,
                Title = title,
                Author = "Author",
                CleanedText = string.Join(" ", Enumerable.Repeat("words", 50)),
                WordCount = 50,
            });
    }

    private void SetupCacheAnalysis(int cached, int uncached)
    {
        _audioCache.AnalyzeCollectionAsync(Arg.Any<IReadOnlyList<(string, string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(new CacheAnalysis
            {
                TotalArticles = cached + uncached,
                CachedArticles = cached,
                UncachedArticles = uncached,
                EstimatedCost = 0.05m,
                ArticleStatuses = [],
            });
    }

    private void SetupAudioCacheMiss()
    {
        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TtsAudioCacheEntry?)null);
    }

    private void SetupAudioCacheHit()
    {
        var path = Path.Combine(_tempDir, $"cached-{Guid.NewGuid():N}.aac");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        _audioCache.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TtsAudioCacheEntry
            {
                CacheKey = "k",
                AudioFilePath = path,
                FileSizeBytes = 4,
                CachedAtUtc = DateTime.UtcNow,
                ContentHash = "h",
                TtsConfigHash = "c",
            });
    }

    private void SetupAudioCachePut()
    {
        _audioCache.PutAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<CancellationToken>())
            .Returns(new TtsAudioCacheEntry
            {
                CacheKey = "new",
                AudioFilePath = Path.Combine(_tempDir, "new.aac"),
                FileSizeBytes = 4,
                CachedAtUtc = DateTime.UtcNow,
                ContentHash = "h",
                TtsConfigHash = "c",
            });
    }

    private void SetupAssemblySuccess()
    {
        _audioAssembler.AssembleAsync(Arg.Any<AssemblyRequest>(), Arg.Any<IProgress<AssemblyProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(AssemblyResult.Successful(
                Path.Combine(_tempDir, "out.m4b"),
                TimeSpan.FromMinutes(10),
                1024,
                new List<AudioChapterMarker>()));
    }

    private void SetupPublishSuccess()
    {
        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Successful("https://example.com/feed.xml", 1));
    }
}
