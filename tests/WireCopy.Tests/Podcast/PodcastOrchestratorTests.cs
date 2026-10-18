// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
    private readonly IPodcastNarrator _narrator;
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

        // Direct dependencies
        _narrator = Substitute.For<IPodcastNarrator>();
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

        _sut = new PodcastOrchestrator(
            contentProvider,
            _narrator,
            _publisher,
            podcastConfig,
            NullLogger<PodcastOrchestrator>.Instance);

        // Default: narrator is configured, estimates succeed, narration writes a small fake audio file
        _narrator.IsConfigured.Returns(true);
        _narrator.EngineName.Returns("openai");
        _narrator.EstimateAsync(Arg.Any<NarrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NarrationEstimate { Success = true });
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => SuccessfulNarration());
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

    private void SetupPublishSuccess(string feedUrl = "https://storage.example.com/feed.xml")
    {
        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Successful(feedUrl, 1));
    }

    private void SetupPublishFailure(string error = "Upload failed")
    {
        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Failure(error));
    }

    /// <summary>
    /// A successful narration whose <see cref="NarrationResult.OutputPath"/> is a small real file
    /// under <see cref="_tempDir"/>, so anything downstream that stats or reads the audio finds it.
    /// </summary>
    private NarrationResult SuccessfulNarration(
        string fileName = "narration.m4b",
        decimal costUsd = 0m,
        IReadOnlyList<NarrationSkippedChapter>? skipped = null)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return new NarrationResult
        {
            Success = true,
            OutputPath = path,
            Bytes = 4,
            Duration = TimeSpan.FromSeconds(30),
            CostUsd = costUsd,
            SkippedChapters = skipped ?? [],
        };
    }

    /// <summary>
    /// A failed narration result carrying the narrator's own message and error kind, the way
    /// audio-engine reports synthesis, assembly and budget problems.
    /// </summary>
    private static NarrationResult FailedNarration(string message, NarrationErrorKind kind = NarrationErrorKind.Failed) => new()
    {
        Success = false,
        ErrorMessage = message,
        ErrorKind = kind,
    };

    /// <summary>
    /// Sets up a complete happy-path pipeline for one article. Narration itself is covered by the
    /// constructor's default <see cref="_narrator"/> stub.
    /// </summary>
    private void SetupHappyPath(string url = "https://example.com/article1")
    {
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
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

    // Guards:   an unconfigured narrator fails fast with an audio-engine message before any extraction
    // Fails if: the IsConfigured preflight is removed or its message no longer names audio-engine
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_TtsNotConfigured_ReturnsFailure"
    [Fact]
    public async Task GeneratePodcastAsync_TtsNotConfigured_ReturnsFailure()
    {
        _narrator.IsConfigured.Returns(false);
        var collection = CreateCollection("https://example.com/a");

        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("audio-engine");
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

    // Guards:   a BudgetExceeded narration result surfaces its 'would cost' message as a failure
    // Fails if: the budget error message is rewritten or the failure swallowed
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_CostExceedsBudget_ReturnsFailure"
    [Fact]
    public async Task GeneratePodcastAsync_CostExceedsBudget_ReturnsFailure()
    {
        var url = "https://example.com/expensive";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        // Cost exceeds the 5.00 budget — audio-engine reports it as a budget failure
        _narrator.NarrateAsync(Arg.Any<NarrationRequest>(), Arg.Any<IProgress<NarrationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(FailedNarration("audio-engine: narration would cost $10.00, over the $5.00 limit.", NarrationErrorKind.BudgetExceeded));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("would cost");
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

    // Guards:   every cached article in a three-article collection is narrated and counted as processed
    // Fails if: the orchestrator drops an article or miscounts ArticlesProcessed
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_MultipleArticles_ProcessesAll"
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
        SetupPublishSuccess();

        var collection = CreateCollection(url1, url2, url3);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(3);
    }

    // Guards:   one cached article becomes one chapter with the article title and its spoken text
    // Fails if: chapter Title or Text stop matching the article / BuildSpokenText
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_HappyPath_SendsChaptersToNarrator"
    [Fact]
    public async Task GeneratePodcastAsync_HappyPath_SendsChaptersToNarrator()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _narrator.Received(1).NarrateAsync(
            Arg.Is<NarrationRequest>(r =>
                r.Chapters.Count == 1 &&
                r.Chapters[0].Title == "Test Article" &&
                r.Chapters[0].Text == PodcastOrchestrator.BuildSpokenText(article)),
            Arg.Any<IProgress<NarrationProgress>?>(),
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
            Arg.Is<PodcastMetadata>(m => m.Title == "Test Podcast"),
            Arg.Is<IReadOnlyList<EpisodeSource>>(e => e.Count == 1),
            Arg.Any<IProgress<PublishProgress>?>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region GeneratePodcastAsync — TTS Failures

    // Guards:   a failed narration result becomes a failure whose message is the narrator's error
    // Fails if: the narrator's ErrorMessage is replaced or ignored
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_SingleArticleTtsFails_ReturnsFailure"
    [Fact]
    public async Task GeneratePodcastAsync_SingleArticleTtsFails_ReturnsFailure()
    {
        var url = "https://example.com/fail";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(FailedNarration("audio-engine: synthesis failed"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("audio-engine: synthesis failed");
    }

    // Guards:   a narration that skips one of two chapters still succeeds with 1 processed and 1 failed
    // Fails if: SkippedChapters stop reducing ArticlesProcessed or raising ArticlesFailed
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_PartialTtsFailure_StillSucceeds"
    [Fact]
    public async Task GeneratePodcastAsync_PartialTtsFailure_StillSucceeds()
    {
        var url1 = "https://example.com/good";
        var url2 = "https://example.com/bad";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Good Article")),
            (url2, CreateExtractedArticle(url2, "Bad Article")));

        // audio-engine narrates the first chapter and skips the second (index 1)
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => SuccessfulNarration(skipped: [new NarrationSkippedChapter(1, "Bad Article", "Quota exceeded")]));

        SetupPublishSuccess();

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.ArticlesFailed.Should().Be(1);
    }

    // Guards:   a skipped chapter appears in FailedArticleDetails with its title, reason and source url
    // Fails if: SkippedChapters are not mapped to ArticleFailure entries
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_PartialTtsFailure_IncludesFailureDetails"
    [Fact]
    public async Task GeneratePodcastAsync_PartialTtsFailure_IncludesFailureDetails()
    {
        var url1 = "https://example.com/good";
        var url2 = "https://example.com/bad";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Good Article")),
            (url2, CreateExtractedArticle(url2, "Bad Article")));

        // audio-engine skips the second chapter (index 1) and says why
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => SuccessfulNarration(skipped: [new NarrationSkippedChapter(1, "Bad Article", "Quota exceeded")]));

        SetupPublishSuccess();

        var collection = CreateCollection(url1, url2);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.FailedArticleDetails.Should().ContainSingle(f => f.Title == "Bad Article");
        result.FailedArticleDetails[0].Reason.Should().Contain("Quota exceeded");
        result.FailedArticleDetails[0].Url.Should().Be(url2);
    }

    #endregion

    #region GeneratePodcastAsync — Assembly Failure

    // Guards:   a narration failure during assembly surfaces its message unchanged
    // Fails if: the orchestrator rewrites or drops the narrator's error message
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_AssemblyFails_ReturnsFailure"
    [Fact]
    public async Task GeneratePodcastAsync_AssemblyFails_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        _narrator.NarrateAsync(Arg.Any<NarrationRequest>(), Arg.Any<IProgress<NarrationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(FailedNarration("audio-engine: assembly failed: FFmpeg crashed", NarrationErrorKind.Failed));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("assembly failed");
        result.ErrorMessage.Should().Contain("FFmpeg crashed");
    }

    #endregion

    #region GeneratePodcastAsync — Publishing

    // Guards:   a failed feed publish still yields a successful local podcast result
    // Fails if: publish errors are turned into an overall failure
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_PublishFails_StillReturnsSuccess"
    [Fact]
    public async Task GeneratePodcastAsync_PublishFails_StillReturnsSuccess()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
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

    // Guards:   the narrator's reported cost is surfaced as the result's TotalCost
    // Fails if: CostUsd from the narration result is dropped or replaced
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_PublishSuccess_IncludesCostInfo"
    [Fact]
    public async Task GeneratePodcastAsync_PublishSuccess_IncludesCostInfo()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
        SetupPublishSuccess();
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => SuccessfulNarration(costUsd: 0.12m));

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

    // Guards:   a token cancelled before narration starts throws OperationCanceledException
    // Fails if: the orchestrator stops checking the token before narrating
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_CancelledBeforeTts_ThrowsOperationCanceledException"
    [Fact]
    public async Task GeneratePodcastAsync_CancelledBeforeTts_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // Guards:   cancellation raised inside the narrator propagates as OperationCanceledException
    // Fails if: the orchestrator swallows the cancellation into a failure result
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_CancelledDuringTts_ThrowsOperationCanceledException"
    [Fact]
    public async Task GeneratePodcastAsync_CancelledDuringTts_ThrowsOperationCanceledException()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        using var cts = new CancellationTokenSource();

        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await cts.CancelAsync();
                callInfo.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return FailedNarration("Should not reach here");
            });

        var collection = CreateCollection(url);
        Func<Task> act = () => _sut.GeneratePodcastAsync(collection, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region GeneratePodcastAsync — Exception Handling

    // Guards:   a non-cancellation exception from the narrator becomes a failure result carrying its message
    // Fails if: the orchestrator lets the exception escape or drops the message
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_UnexpectedExceptionDuringTts_ReturnsFailure"
    [Fact]
    public async Task GeneratePodcastAsync_UnexpectedExceptionDuringTts_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    // Guards:   an exception thrown by the publisher becomes a failure result instead of escaping
    // Fails if: the orchestrator stops catching publisher exceptions
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_PublisherThrowsException_ReturnsFailure"
    [Fact]
    public async Task GeneratePodcastAsync_PublisherThrowsException_ReturnsFailure()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        _publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(),
                Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Connection refused");
    }

    #endregion

    #region GeneratePodcastAsync — Cache Reuse from AnalyzeCacheStatusAsync

    // Guards:   articles extracted during AnalyzeCacheStatusAsync are reused by a following generate for the same collection
    // Fails if: the generate pass re-extracts articles it already has
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ReusesArticlesFromAnalyze_WhenCollectionNameMatches"
    [Fact]
    public async Task GeneratePodcastAsync_ReusesArticlesFromAnalyze_WhenCollectionNameMatches()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
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

    // Guards:   articles cached by analyze for one collection are not reused for a differently named collection
    // Fails if: the collection-name check on the reuse path is dropped
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_DoesNotReuseCachedArticles_WhenCollectionNameDiffers"
    [Fact]
    public async Task GeneratePodcastAsync_DoesNotReuseCachedArticles_WhenCollectionNameDiffers()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
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

    // Guards:   reused analyze articles are cleared once consumed, so the next generate extracts fresh
    // Fails if: the orchestrator keeps the analyze cache after using it
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ClearsCachedArticlesAfterUse"
    [Fact]
    public async Task GeneratePodcastAsync_ClearsCachedArticlesAfterUse()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));
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

    // Guards:   a generate that starts while analyze is still running waits for it instead of re-extracting
    // Fails if: the pending-analysis await is removed
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_AwaitsPendingAnalysis_WhenAnalysisStillRunning"
    [Fact]
    public async Task GeneratePodcastAsync_AwaitsPendingAnalysis_WhenAnalysisStillRunning()
    {
        // Simulate: AnalyzeCacheStatusAsync started but the user skips the UI
        // before extraction finishes. GeneratePodcastAsync should await the
        // pending task and reuse its cached articles — NOT call extraction again.
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);

        // Use a TCS to control when the article cache responds, simulating slow extraction
        var extractionGate = new TaskCompletionSource<ExtractedArticle?>();
        var callCount = 0;
        _articleCache.TryGetAsync(url, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var currentCall = Interlocked.Increment(ref callCount);
                if (currentCall == 1)
                {
                    // First call (from AnalyzeCacheStatusAsync) — block until gate opens
                    return extractionGate.Task;
                }

                // Subsequent calls should NOT happen if caching works correctly,
                // but return the article if they do so the test doesn't hang
                return Task.FromResult<ExtractedArticle?>(article);
            });

        SetupPublishSuccess();

        var collection = CreateCollection(url);

        // Start analysis but do NOT await it — simulates user skipping the analysis UI
        var analysisTask = _sut.AnalyzeCacheStatusAsync(collection);

        // At this point the analysis task is blocked on the extraction gate.
        // Now call GeneratePodcastAsync which should detect the pending analysis
        // and await it rather than re-extracting.
        var generateTask = _sut.GeneratePodcastAsync(collection);

        // Release the gate so the analysis extraction completes
        extractionGate.SetResult(article);

        // Both tasks should now complete
        await analysisTask;
        var result = await generateTask;

        result.Success.Should().BeTrue();

        // The article cache should have been called exactly once — during the
        // analysis phase. GeneratePodcastAsync should have reused the cached
        // articles from analysis rather than calling extraction again.
        callCount.Should().Be(1,
            "GetAllArticleContentAsync should only have been called once (during AnalyzeCacheStatusAsync); " +
            "GeneratePodcastAsync should reuse cached articles from the pending analysis");
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

    // Guards:   a narrator estimate with every chunk cached reports the article as cached
    // Fails if: ChunksCached == ChunksTotal no longer counts as a cached article
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.AnalyzeCacheStatusAsync_WithArticles_ReturnsCacheAnalysis"
    [Fact]
    public async Task AnalyzeCacheStatusAsync_WithArticles_ReturnsCacheAnalysis()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        _narrator.EstimateAsync(Arg.Any<NarrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NarrationEstimate
            {
                Success = true,
                Chapters = [new NarrationChapterEstimate(0, "Test Article", 3, 3, 0m)],
            });

        var collection = CreateCollection(url);
        var result = await _sut.AnalyzeCacheStatusAsync(collection);

        result.TotalArticles.Should().Be(1);
        result.CachedArticles.Should().Be(1);
    }

    // Guards:   an exception from EstimateAsync yields an empty analysis instead of escaping
    // Fails if: the orchestrator stops catching estimate exceptions
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.AnalyzeCacheStatusAsync_ExceptionDuringAnalysis_ReturnsEmptyAnalysis"
    [Fact]
    public async Task AnalyzeCacheStatusAsync_ExceptionDuringAnalysis_ReturnsEmptyAnalysis()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        _narrator.EstimateAsync(Arg.Any<NarrationRequest>(), Arg.Any<CancellationToken>())
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

    // Guards:   AnalyzeCacheStatusAsync reports extraction progress to the caller
    // Fails if: the progress sink is no longer invoked during analysis
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.AnalyzeCacheStatusAsync_ReportsProgress"
    [Fact]
    public async Task AnalyzeCacheStatusAsync_ReportsProgress()
    {
        var url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        SetupArticleCacheHits((url, article));

        var progressReports = new List<ContentExtractionProgress>();
        var progress = new SynchronousProgress<ContentExtractionProgress>(p => progressReports.Add(p));

        var collection = CreateCollection(url);
        await _sut.AnalyzeCacheStatusAsync(collection, progress);

        progressReports.Should().NotBeEmpty();
    }

    #endregion

    #region GeneratePodcastAsync — Assembly Request Metadata

    // Guards:   the NarrationRequest carries the podcast config's title, author, album and genre
    // Fails if: request metadata is no longer filled from PodcastConfiguration
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_PassesPodcastConfigToNarrator"
    [Fact]
    public async Task GeneratePodcastAsync_PassesPodcastConfigToNarrator()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _narrator.Received(1).NarrateAsync(
            Arg.Is<NarrationRequest>(r =>
                r.Title.Contains("Test Podcast") &&
                r.Author == "Test Author" &&
                r.Album == "Test Podcast" &&
                r.Genre == "Podcast"),
            Arg.Any<IProgress<NarrationProgress>?>(),
            Arg.Any<CancellationToken>());
    }

    // Guards:   the narration output path includes the collection name
    // Fails if: the output path is built without the collection name
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_AssemblyOutputPathIncludesCollectionName"
    [Fact]
    public async Task GeneratePodcastAsync_AssemblyOutputPathIncludesCollectionName()
    {
        var url = "https://example.com/article1";
        SetupHappyPath(url);

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _narrator.Received(1).NarrateAsync(
            Arg.Is<NarrationRequest>(r => r.OutputPath.Contains("Test Collection")),
            Arg.Any<IProgress<NarrationProgress>?>(),
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
            Arg.Any<IProgress<PublishProgress>?>(),
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
            Arg.Any<IProgress<PublishProgress>?>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region GeneratePodcastAsync — Extraction Failures Propagation

    // Guards:   articles that failed extraction appear in FailedArticleDetails of a successful result
    // Fails if: extraction failures are dropped from the result
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ExtractionFailures_IncludedInSuccessResult"
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

        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, badUrl);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        // Extraction failures from the content provider should appear in FailedArticleDetails
        result.FailedArticleDetails.Should().Contain(f => f.Url == badUrl);
    }

    // Guards:   extraction failures recorded during analyze are carried into the following generate's result
    // Fails if: the reuse path forgets analyze-time failures
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_CachedExtractionFailures_CarriedFromAnalyze"
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

    // Guards:   narrator chapter progress maps to GeneratingAudio reports with the right TotalArticles and CurrentArticle
    // Fails if: ChapterIndex/ChaptersTotal are no longer translated into PodcastProgress fields
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ProgressReportsCorrectArticleCounts"
    [Fact]
    public async Task GeneratePodcastAsync_ProgressReportsCorrectArticleCounts()
    {
        var url1 = "https://example.com/article1";
        var url2 = "https://example.com/article2";

        SetupArticleCacheHits(
            (url1, CreateExtractedArticle(url1, "Article 1")),
            (url2, CreateExtractedArticle(url2, "Article 2")));
        SetupPublishSuccess();

        // The narrator reports one Synthesizing step per chapter; the orchestrator must map
        // ChapterIndex/ChaptersTotal onto CurrentArticle/TotalArticles.
        _narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(),
                Arg.Any<IProgress<NarrationProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var sink = ci.ArgAt<IProgress<NarrationProgress>?>(1);
                sink?.Report(new NarrationProgress
                {
                    Phase = NarrationPhase.Synthesizing,
                    Fraction = 0.2,
                    ChapterIndex = 0,
                    ChapterTitle = "Article 1",
                    ChaptersTotal = 2,
                });
                sink?.Report(new NarrationProgress
                {
                    Phase = NarrationPhase.Synthesizing,
                    Fraction = 0.6,
                    ChapterIndex = 1,
                    ChapterTitle = "Article 2",
                    ChaptersTotal = 2,
                });
                return SuccessfulNarration();
            });

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

    // Guards:   an article under the minimum word count is skipped by the quality gate
    // Fails if: the word-count gate is removed or its threshold lowered
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ArticleBelowMinimumWordCount_IsSkipped"
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
        SetupPublishSuccess();

        var collection = CreateCollection(goodUrl, shortUrl);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
        result.FailedArticleDetails.Should().Contain(f =>
            f.Title == "Short Article" && f.Reason.Contains("too short"));
    }

    // Guards:   an article with an empty title is skipped by the quality gate
    // Fails if: the title check in the quality gate is removed
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ArticleWithEmptyTitle_IsSkipped"
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

    // Guards:   an article whose WordCount is 0 is judged by counting its text instead of being skipped
    // Fails if: the quality gate treats WordCount == 0 as too short without counting words
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ArticleWithZeroWordCountProperty_FallsBackToTextCounting"
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
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
    }

    // Guards:   an article exactly at the minimum word count passes the quality gate
    // Fails if: the gate uses > instead of >= at the threshold
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_ArticleExactlyAtMinimumWordCount_IsNotSkipped"
    [Fact]
    public async Task GeneratePodcastAsync_ArticleExactlyAtMinimumWordCount_IsNotSkipped()
    {
        var url = "https://example.com/exact";
        // Build a string with exactly 100 words
        var words = Enumerable.Range(1, 100).Select(i => $"word{i}");
        var exactText = string.Join(" ", words);

        var article = CreateExtractedArticle(url, "Exact Minimum", exactText);

        SetupArticleCacheHits((url, article));
        SetupPublishSuccess();

        var collection = CreateCollection(url);
        var result = await _sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue();
        result.ArticlesProcessed.Should().Be(1);
    }

    // Guards:   when every article fails the quality gate, the narrator is never called
    // Fails if: the orchestrator narrates an empty chapter list
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorTests.GeneratePodcastAsync_QualitySkippedArticle_DoesNotCallTts"
    [Fact]
    public async Task GeneratePodcastAsync_QualitySkippedArticle_DoesNotCallTts()
    {
        var url = "https://example.com/short";
        SetupArticleCacheHits(
            (url, CreateExtractedArticle(url, "Short Article", "Just three words.")));

        var collection = CreateCollection(url);
        await _sut.GeneratePodcastAsync(collection);

        await _narrator.DidNotReceive().NarrateAsync(
            Arg.Any<NarrationRequest>(),
            Arg.Any<IProgress<NarrationProgress>?>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Output Folder Resolution (workspace-urko)

    /// <summary>
    /// Builds a fresh PodcastOrchestrator with a substitute settings store so we can
    /// exercise the runtime override path independently of the shared fixture.
    /// </summary>
    private (PodcastOrchestrator sut, IUserSettingsStore store) BuildOrchestratorWithSettingsStore(
        PodcastConfiguration? podcastConfig = null)
    {
        var store = Substitute.For<IUserSettingsStore>();
        var pageLoader = Substitute.For<IPageLoader>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();
        var preloadService = Substitute.For<IPreloadService>();
        var pageCache = Substitute.For<IPageCache>();
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        var articleCache = Substitute.For<IArticleContentCache>();

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
            articleCache,
            NullLogger<ReadingListContentProvider>.Instance);

        var narrator = Substitute.For<IPodcastNarrator>();
        narrator.IsConfigured.Returns(true);
        narrator.EngineName.Returns("openai");
        narrator.EstimateAsync(Arg.Any<NarrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NarrationEstimate { Success = true });
        var publisher = Substitute.For<IPodcastPublisher>();

        var pConfig = Options.Create(podcastConfig ?? new PodcastConfiguration
        {
            Title = "T",
            Description = "D",
            Author = "A",
            Language = "en-us",
            Category = "Tech",
            // Leave OutputFolderPath null → falls back to LocalAppData default in ResolveOutputFolderPath.
        });

        var sut = new PodcastOrchestrator(
            contentProvider,
            narrator,
            publisher,
            pConfig,
            NullLogger<PodcastOrchestrator>.Instance,
            store);

        return (sut, store);
    }

    [Fact]
    public void ResolveEffectiveOutputFolder_WithTildePrefix_ExpandsToHome()
    {
        var (sut, store) = BuildOrchestratorWithSettingsStore();
        store.Get("PodcastOutputFolder").Returns("~/podcasts");

        var path = sut.GetOutputFilePath("My Collection");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        path.Should().StartWith(Path.Combine(home, "podcasts"),
            "tilde prefix must expand to the user's home directory");

        // Cleanup: GetOutputFilePath calls Directory.CreateDirectory(folder) so we may
        // have created an empty {home}/podcasts directory. Best-effort delete.
        try
        {
            var folder = Path.Combine(home, "podcasts");
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void ResolveEffectiveOutputFolder_WithAbsolutePath_ReturnsAsIs()
    {
        // Use a unique temp absolute path so the test is hermetic.
        var abs = Path.Combine(Path.GetTempPath(), $"urko-abs-{Guid.NewGuid():N}");
        try
        {
            var (sut, store) = BuildOrchestratorWithSettingsStore();
            store.Get("PodcastOutputFolder").Returns(abs);

            var path = sut.GetOutputFilePath("Coll");

            path.Should().StartWith(abs, "absolute paths must be returned without tilde rewriting");
            path.Should().EndWith("Coll.m4a");
        }
        finally
        {
            try
            {
                if (Directory.Exists(abs))
                {
                    Directory.Delete(abs, recursive: true);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void ResolveEffectiveOutputFolder_WithRelativePath_ReturnsAsIs()
    {
        // The implementation does not normalize relative paths — it returns them
        // verbatim (they're resolved against CWD by Path.Combine downstream).
        // Use a unique tmp-relative name so cleanup is safe.
        var rel = $"urko-rel-{Guid.NewGuid():N}";
        try
        {
            var (sut, store) = BuildOrchestratorWithSettingsStore();
            store.Get("PodcastOutputFolder").Returns(rel);

            var path = sut.GetOutputFilePath("Coll");

            // Path.Combine(rel, "Coll.m4a") — relative path is preserved.
            path.Should().Be(Path.Combine(rel, "Coll.m4a"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(rel))
                {
                    Directory.Delete(rel, recursive: true);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void ResolveEffectiveOutputFolder_NoSavedValue_FallsBackToConfig()
    {
        var configured = Path.Combine(Path.GetTempPath(), $"urko-cfg-{Guid.NewGuid():N}");
        try
        {
            var podcastConfig = new PodcastConfiguration
            {
                Title = "T",
                Description = "D",
                Author = "A",
                Language = "en-us",
                Category = "Tech",
                OutputFolderPath = configured,
            };
            var (sut, store) = BuildOrchestratorWithSettingsStore(podcastConfig);
            store.Get("PodcastOutputFolder").Returns((string?)null);

            var path = sut.GetOutputFilePath("Coll");

            path.Should().Be(Path.Combine(configured, "Coll.m4a"),
                "with no override, the bound PodcastConfiguration must win");
        }
        finally
        {
            try
            {
                if (Directory.Exists(configured))
                {
                    Directory.Delete(configured, recursive: true);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void ResolveEffectiveOutputFolder_EmptySavedValue_FallsBackToConfig()
    {
        var configured = Path.Combine(Path.GetTempPath(), $"urko-cfg-empty-{Guid.NewGuid():N}");
        try
        {
            var podcastConfig = new PodcastConfiguration
            {
                Title = "T",
                Description = "D",
                Author = "A",
                Language = "en-us",
                Category = "Tech",
                OutputFolderPath = configured,
            };
            var (sut, store) = BuildOrchestratorWithSettingsStore(podcastConfig);
            store.Get("PodcastOutputFolder").Returns(string.Empty);

            var path = sut.GetOutputFilePath("Coll");

            path.Should().Be(Path.Combine(configured, "Coll.m4a"),
                "empty/whitespace overrides must be ignored (treated as unset)");
        }
        finally
        {
            try
            {
                if (Directory.Exists(configured))
                {
                    Directory.Delete(configured, recursive: true);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }

    #endregion

    #region Publish-Failure Classification (workspace-3a2k Phase E)

    /// <summary>
    /// workspace-3a2k Phase E: when a GCS bucket IS configured and publish
    /// fails, the orchestrator must return a TOTAL failure (Success=false) so
    /// the result screen can render Shape D with bucket-public remediation —
    /// NOT a "local-only success" that hides the broken feed URL behind a
    /// ✓ headline. Previously the orchestrator silently degraded any publish
    /// failure to LocalOnlySuccess regardless of whether the user had
    /// configured GCS.
    /// </summary>
    [Fact]
    public async Task GeneratePodcastAsync_PublishFails_WithBucketConfigured_ReturnsTotalFailureWithTypedDetail()
    {
        var (sut, store, publisher, narrator, articleCache) = BuildOrchestratorAllSubs();
        store.Get("GcsBucketName").Returns("my-bucket");

        const string url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        articleCache.TryGetAsync(url, Arg.Any<CancellationToken>()).Returns(article);

        narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(), Arg.Any<IProgress<NarrationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulNarration());

        publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Failure(
                "Anonymous HTTP GET returned 403 — bucket may not grant allUsers:objectViewer.",
                FeedPublishFailureClass.BucketNotPublic));

        var collection = CreateCollection(url);
        var result = await sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse(
            "publish failure with a configured bucket must surface as TotalFailure — not a misleading 'local-only success'");
        result.Classification.Should().Be(PodcastResultClassification.TotalFailure);
        result.FailureDetail.Should().NotBeNull();
        result.FailureDetail!.Step.Should().Be("Publishing");
        result.FailureDetail.FailureClass.Should().Be(FeedPublishFailureClass.BucketNotPublic);
        result.FailureDetail.RemediationCopy.Should().Contain("allUsers:objectViewer",
            "Shape D's Fix line must direct the user to the bucket-public IAM grant");
        result.LocalFilePath.Should().NotBeNullOrEmpty(
            "the M4B still got written locally — surface its path so the user can recover it");
        result.FeedUrl.Should().BeNull(
            "a publish that failed reachability MUST NOT advertise a broken feed URL — the whole point of the bead");
    }

    /// <summary>
    /// Inverse: when NO bucket is configured, a publish failure still falls
    /// back to the legacy LocalOnlySuccess path (the publish attempt was
    /// best-effort; the user never asked for it). Pinned so the new
    /// classification logic doesn't regress the local-only flow.
    /// </summary>
    [Fact]
    public async Task GeneratePodcastAsync_PublishFails_WithoutBucketConfigured_StillReturnsLocalOnlySuccess()
    {
        var (sut, store, publisher, narrator, articleCache) = BuildOrchestratorAllSubs();
        store.Get("GcsBucketName").Returns((string?)null);

        const string url = "https://example.com/article1";
        var article = CreateExtractedArticle(url);
        articleCache.TryGetAsync(url, Arg.Any<CancellationToken>()).Returns(article);

        narrator.NarrateAsync(
                Arg.Any<NarrationRequest>(), Arg.Any<IProgress<NarrationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulNarration());
        publisher.PublishFeedAsync(
                Arg.Any<PodcastMetadata>(), Arg.Any<IReadOnlyList<EpisodeSource>>(),
                Arg.Any<IProgress<PublishProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(FeedPublishResult.Failure("No bucket configured — running local-only"));

        var collection = CreateCollection(url);
        var result = await sut.GeneratePodcastAsync(collection);

        result.Success.Should().BeTrue(
            "without a configured bucket the publish attempt is best-effort; the M4B still represents a successful local-only run");
        result.FeedUrl.Should().BeNull();
        result.FailureDetail.Should().BeNull();
    }

    /// <summary>
    /// Builds an orchestrator with substitutes for every collaborator we need
    /// to drive a full pipeline + a settings store so the publish-failure
    /// classification path is exercisable. The shared <see cref="_sut"/>
    /// fixture lacks the settings store, which is required to hit the new
    /// workspace-3a2k branch.
    /// </summary>
    private (
        PodcastOrchestrator sut,
        IUserSettingsStore store,
        IPodcastPublisher publisher,
        IPodcastNarrator narrator,
        IArticleContentCache articleCache) BuildOrchestratorAllSubs()
    {
        var store = Substitute.For<IUserSettingsStore>();
        var pageLoader = Substitute.For<IPageLoader>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();
        var preloadService = Substitute.For<IPreloadService>();
        var pageCache = Substitute.For<IPageCache>();
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        var articleCache = Substitute.For<IArticleContentCache>();

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
            articleCache,
            NullLogger<ReadingListContentProvider>.Instance);

        var narrator = Substitute.For<IPodcastNarrator>();
        narrator.IsConfigured.Returns(true);
        narrator.EngineName.Returns("openai");
        narrator.EstimateAsync(Arg.Any<NarrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NarrationEstimate { Success = true });
        var publisher = Substitute.For<IPodcastPublisher>();

        var pConfig = Options.Create(new PodcastConfiguration
        {
            Title = "Test Podcast",
            Description = "Test podcast description",
            Author = "Test Author",
            Language = "en-us",
            Category = "Technology",
            TempDirectory = _tempDir,
        });

        var sut = new PodcastOrchestrator(
            contentProvider,
            narrator,
            publisher,
            pConfig,
            NullLogger<PodcastOrchestrator>.Instance,
            store);

        return (sut, store, publisher, narrator, articleCache);
    }

    #endregion
}

[Trait("Category", "Unit")]
public class BuildSpokenTextTests
{
    [Fact]
    public void BuildSpokenText_IncludesTitle()
    {
        var article = new ExtractedArticle
        {
            Title = "Test Headline",
            CleanedText = "Article body text.",
            Url = "https://example.com/article",
        };

        var result = PodcastOrchestrator.BuildSpokenText(article);

        result.Should().StartWith("Test Headline.\n");
        result.Should().EndWith("Article body text.");
    }

    [Fact]
    public void BuildSpokenText_IncludesAuthor()
    {
        var article = new ExtractedArticle
        {
            Title = "Headline",
            CleanedText = "Body.",
            Url = "https://example.com",
            Author = "Jane Doe",
        };

        var result = PodcastOrchestrator.BuildSpokenText(article);

        result.Should().Contain("By Jane Doe.");
    }

    [Fact]
    public void BuildSpokenText_IncludesPublishedDate()
    {
        var article = new ExtractedArticle
        {
            Title = "Headline",
            CleanedText = "Body.",
            Url = "https://example.com",
            PublishedDate = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc),
        };

        var result = PodcastOrchestrator.BuildSpokenText(article);

        result.Should().Contain("Published March 14, 2026.");
    }

    [Fact]
    public void BuildSpokenText_OmitsAuthorWhenNull()
    {
        var article = new ExtractedArticle
        {
            Title = "Headline",
            CleanedText = "Body.",
            Url = "https://example.com",
        };

        var result = PodcastOrchestrator.BuildSpokenText(article);

        result.Should().NotContain("By ");
    }

    [Fact]
    public void BuildSpokenText_OmitsDateWhenNull()
    {
        var article = new ExtractedArticle
        {
            Title = "Headline",
            CleanedText = "Body.",
            Url = "https://example.com",
        };

        var result = PodcastOrchestrator.BuildSpokenText(article);

        result.Should().NotContain("Published");
    }

    [Fact]
    public void BuildSpokenText_FullIntro()
    {
        var article = new ExtractedArticle
        {
            Title = "U.S. Vows to Block Iran",
            CleanedText = "The article continues here.",
            Url = "https://example.com",
            Author = "Thomas Fuller",
            PublishedDate = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc),
        };

        var result = PodcastOrchestrator.BuildSpokenText(article);

        result.Should().Be(
            "U.S. Vows to Block Iran.\n" +
            "By Thomas Fuller.\n" +
            "Published March 14, 2026.\n" +
            "\n" +
            "The article continues here.");
    }
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
