// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.DTOs.Browser;
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
/// Tests for workspace-74zy (Phase A: surface real progress signals), migrated to the
/// <see cref="IPodcastNarrator"/> seam (workspace-c3ec4.2). Asserts the orchestrator maps
/// <see cref="NarrationProgress"/> events (chunk counts, the Assembling phase) and
/// <see cref="PublishProgress"/> per-episode upload events through the
/// <see cref="PodcastProgress"/> sink, and that <c>PhaseElapsed</c> is monotonic.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastOrchestratorProgressTests : IDisposable
{
    private readonly IArticleContentCache _articleCache;
    private readonly IPodcastNarrator _narrator;
    private readonly IPodcastPublisher _publisher;
    private readonly PodcastOrchestrator _sut;
    private readonly string _tempDir;
    private readonly string _fakeAudioFile;

    public PodcastOrchestratorProgressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"podcast-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fakeAudioFile = Path.Combine(_tempDir, "narrated.m4a");
        File.WriteAllBytes(_fakeAudioFile, [1, 2, 3, 4]);

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

        _narrator = Substitute.For<IPodcastNarrator>();
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

        _narrator.IsConfigured.Returns(true);
        _narrator.EngineName.Returns("openai");
        _narrator.EstimateAsync(Arg.Any<NarrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NarrationEstimate { Success = true });
        SetupNarrateSuccess();

        _sut = new PodcastOrchestrator(
            contentProvider,
            _narrator,
            _publisher,
            podcastConfig,
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

    // Guards:   chunk-level progress from the narrator (ChunksDone/ChunksTotal) reaches the PodcastProgress
    //           sink as CurrentArticleChunkIndex/Total/Percent, so the UI can show "chunk 2 of 2".
    // Fails if: MapNarrationProgress stops copying the chunk fields, or the orchestrator stops passing an
    //           IProgress<NarrationProgress> to NarrateAsync.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorProgressTests.GeneratePodcast_ForwardsNarratorChunkProgress_IntoPodcastProgress"
    [Fact]
    public async Task GeneratePodcast_ForwardsNarratorChunkProgress_IntoPodcastProgress()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupPublishSuccess();

        // Narrator simulates two chunk progress reports for the single chapter.
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sink = callInfo.ArgAt<IProgress<NarrationProgress>?>(1);
                sink?.Report(new NarrationProgress
                {
                    Phase = NarrationPhase.Synthesizing,
                    Fraction = 0.25,
                    ChapterIndex = 0,
                    ChapterTitle = "Article 1",
                    ChaptersTotal = 1,
                    ChunksDone = 1,
                    ChunksTotal = 2,
                    Message = "Chunk 1/2",
                });
                sink?.Report(new NarrationProgress
                {
                    Phase = NarrationPhase.Synthesizing,
                    Fraction = 0.5,
                    ChapterIndex = 0,
                    ChapterTitle = "Article 1",
                    ChaptersTotal = 1,
                    ChunksDone = 2,
                    ChunksTotal = 2,
                    Message = "Chunk 2/2",
                });
                return Task.FromResult(SuccessfulNarration());
            });

        var events = new List<PodcastProgress>();
        var progress = new SynchronousProgress<PodcastProgress>(p => events.Add(p));

        var result = await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        result.Success.Should().BeTrue();
        var chunkEvents = events.Where(e => e.CurrentArticleChunkTotal > 0).ToList();
        chunkEvents.Should().HaveCountGreaterThanOrEqualTo(2);
        chunkEvents.Should().Contain(e => e.CurrentArticleChunkIndex == 1 && e.CurrentArticleChunkTotal == 2);
        chunkEvents.Should().Contain(e => e.CurrentArticleChunkIndex == 2 && e.CurrentArticleChunkTotal == 2);
        chunkEvents.Last().CurrentArticleChunkPercent.Should().BeApproximately(100.0, 0.01);
        chunkEvents.Should().OnlyContain(e => e.Phase == PodcastPhase.GeneratingAudio);
        chunkEvents.Should().OnlyContain(e => e.CurrentArticle == 1 && e.ArticleTitle == "Article 1");
    }

    // Guards:   the narrator's Assembling phase shows up as the AssemblingAudio podcast phase at 70%, and its
    //           Done phase is NOT forwarded (publishing reports its own bands after it).
    // Fails if: MapNarrationProgress maps Assembling to GeneratingAudio, changes the 70% band, or the
    //           orchestrator starts forwarding NarrationPhase.Done as an audio-phase event.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorProgressTests.GeneratePodcast_ForwardsAssemblingPhase_AsAssemblingAudioPhase"
    [Fact]
    public async Task GeneratePodcast_ForwardsAssemblingPhase_AsAssemblingAudioPhase()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupPublishSuccess();

        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sink = callInfo.ArgAt<IProgress<NarrationProgress>?>(1);
                sink?.Report(new NarrationProgress { Phase = NarrationPhase.Synthesizing, Fraction = 0.5, ChapterIndex = 0, ChaptersTotal = 1 });
                sink?.Report(new NarrationProgress { Phase = NarrationPhase.Assembling, Fraction = 0.95, ChaptersTotal = 1, Message = "Joining chapters" });
                sink?.Report(new NarrationProgress { Phase = NarrationPhase.Done, Fraction = 1.0, ChaptersTotal = 1 });
                return Task.FromResult(SuccessfulNarration());
            });

        var events = new List<PodcastProgress>();
        var progress = new SynchronousProgress<PodcastProgress>(p => events.Add(p));

        await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        var assembly = events.Where(p => p.Phase == PodcastPhase.AssemblingAudio).ToList();
        assembly.Should().ContainSingle();
        assembly[0].PercentComplete.Should().Be(70);
        assembly[0].TotalArticles.Should().Be(1);
        assembly[0].Message.Should().Be("Joining chapters");

        // Done is dropped: after the Assembling event the next event belongs to publishing.
        var assemblingIndex = events.IndexOf(assembly[0]);
        events.Skip(assemblingIndex + 1).Should().OnlyContain(e => e.Phase != PodcastPhase.GeneratingAudio && e.Phase != PodcastPhase.AssemblingAudio);
    }

    // Guards:   per-episode upload progress from the publisher reaches the sink as Publishing-phase events
    //           carrying UploadedBytes/UploadedBytesTotal and UploadedEpisodes/UploadedEpisodesTotal.
    // Fails if: the orchestrator stops passing an IProgress<PublishProgress> to PublishFeedAsync or stops
    //           copying the upload counters into PodcastProgress.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorProgressTests.GeneratePodcast_ForwardsPublishProgress_AsPublishingPhase"
    [Fact]
    public async Task GeneratePodcast_ForwardsPublishProgress_AsPublishingPhase()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");

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
        var progress = new SynchronousProgress<PodcastProgress>(p => events.Add(p));

        await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        var publish = events.Where(p => p.Phase == PodcastPhase.Publishing).ToList();
        publish.Should().Contain(p => p.UploadedBytes == 512 && p.UploadedBytesTotal == 1024);
        publish.Should().Contain(p => p.UploadedEpisodes == 1 && p.UploadedEpisodesTotal == 1);
    }

    // Guards:   PhaseElapsed never goes backwards across GeneratingAudio events in one run (it reads one
    //           per-phase stopwatch), so a weighted-ETA consumer can trust it.
    // Fails if: the orchestrator restarts or swaps the audio-phase stopwatch between the initial
    //           "Sending articles" report and the narrator-driven reports.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorProgressTests.GeneratePodcast_PhaseElapsed_IsMonotonicWithinAudioPhase"
    [Fact]
    public async Task GeneratePodcast_PhaseElapsed_IsMonotonicWithinAudioPhase()
    {
        const string url = "https://example.com/article1";
        SetupArticleCacheHit(url, "Article 1");
        SetupPublishSuccess();

        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sink = callInfo.ArgAt<IProgress<NarrationProgress>?>(1);
                sink?.Report(new NarrationProgress { Phase = NarrationPhase.Queued, Fraction = 0, ChaptersTotal = 1 });
                sink?.Report(new NarrationProgress { Phase = NarrationPhase.Synthesizing, Fraction = 0.3, ChapterIndex = 0, ChaptersTotal = 1 });
                sink?.Report(new NarrationProgress { Phase = NarrationPhase.Synthesizing, Fraction = 0.6, ChapterIndex = 0, ChaptersTotal = 1 });
                return Task.FromResult(SuccessfulNarration());
            });

        var events = new List<PodcastProgress>();
        var progress = new SynchronousProgress<PodcastProgress>(p => events.Add(p));

        await _sut.GeneratePodcastAsync(CreateCollection(url), progress);

        var audioElapsed = events.Where(p => p.Phase == PodcastPhase.GeneratingAudio).Select(p => p.PhaseElapsed).ToList();
        audioElapsed.Should().HaveCountGreaterThanOrEqualTo(3);
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

    private NarrationResult SuccessfulNarration() => new()
    {
        Success = true,
        OutputPath = _fakeAudioFile,
        Bytes = 4,
        Duration = TimeSpan.FromSeconds(30),
        CostUsd = 0m,
    };

    private void SetupNarrateSuccess()
    {
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(SuccessfulNarration());
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
