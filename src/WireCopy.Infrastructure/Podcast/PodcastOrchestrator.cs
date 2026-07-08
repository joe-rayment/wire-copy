// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs;
using WireCopy.Application.DTOs.Audio;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Audio;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.ValueObjects.Audio;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Storage;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Orchestrates the end-to-end podcast generation pipeline:
/// content extraction, TTS generation, M4B assembly, and feed publishing.
/// </summary>
internal sealed class PodcastOrchestrator : IPodcastOrchestrator
{
    /// <summary>
    /// workspace-hzjs (F.3): how often the orchestrator allows the
    /// <see cref="PodcastJobJournal"/> to push a PodcastProgress snapshot
    /// to the SQLite row. Tuned so multi-byte upload events don't hammer
    /// the DB — 1s is fast enough that a UI restoring from the row sees
    /// a fresh status, slow enough that an 8-minute generation produces
    /// ~500 writes, not 5,000.
    /// </summary>
    private static readonly TimeSpan JobJournalDebounceInterval = TimeSpan.FromSeconds(1);

    private readonly ReadingListContentProvider _contentProvider;
    private readonly ITtsService _ttsService;
    private readonly ITtsAudioCache _audioCache;
    private readonly IAudioAssembler _audioAssembler;
    private readonly IPodcastPublisher _publisher;
    private readonly PodcastConfiguration _podcastConfig;
    private readonly OpenAiTtsConfiguration _ttsConfig;
    private readonly IUserSettingsStore? _settingsStore;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<PodcastOrchestrator> _logger;

    // Cache extracted articles from AnalyzeCacheStatusAsync so GeneratePodcastAsync
    // can reuse them instead of re-extracting (each extraction has rate-limit delays).
    private IReadOnlyList<ExtractedArticle>? _cachedArticles;
    private IReadOnlyList<ArticleFailure>? _cachedExtractionFailures;
    private string? _cachedCollectionName;

    // When the user skips the analysis UI, the extraction may still be running.
    // Store the task so GeneratePodcastAsync can await it instead of re-extracting.
    private Task<CacheAnalysis>? _pendingAnalysisTask;
    private string? _pendingAnalysisCollection;

    public PodcastOrchestrator(
        ReadingListContentProvider contentProvider,
        ITtsService ttsService,
        ITtsAudioCache audioCache,
        IAudioAssembler audioAssembler,
        IPodcastPublisher publisher,
        IOptions<PodcastConfiguration> podcastConfig,
        IOptions<OpenAiTtsConfiguration> ttsConfig,
        ILogger<PodcastOrchestrator> logger,
        IUserSettingsStore? settingsStore = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _contentProvider = contentProvider;
        _ttsService = ttsService;
        _audioCache = audioCache;
        _audioAssembler = audioAssembler;
        _publisher = publisher;
        _podcastConfig = podcastConfig.Value;
        _ttsConfig = ttsConfig.Value;
        _settingsStore = settingsStore;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the absolute output folder path (creating it on demand) and
    /// returns the file path the next M4B should be written to.
    /// </summary>
    public string GetOutputFilePath(string collectionName)
    {
        var folder = ResolveEffectiveOutputFolder();
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{SanitizeFileName(collectionName)}.m4a");
    }

    /// <summary>
    /// Resolves the local M4B path AND the public feed URL the upcoming
    /// generation will write to (workspace-zh3u). If no GCS bucket is
    /// configured, FeedUrl is null and the caller should render a
    /// local-only footer line. Errors resolving the feed URL (e.g. SA key
    /// problems) are caught here so the progress screen never crashes on a
    /// non-critical lookup; the footer falls back to local-only in that
    /// case.
    /// </summary>
    public async Task<PodcastTargets> ResolveTargetsAsync(
        Collection collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var localPath = GetOutputFilePath(collection.Name);

        var bucketConfigured = !string.IsNullOrWhiteSpace(_settingsStore?.Get("GcsBucketName"));
        if (!bucketConfigured)
        {
            return new PodcastTargets { LocalFilePath = localPath, FeedUrl = null };
        }

        try
        {
            var feedUrl = await _publisher.ResolveFeedUrlAsync(_podcastConfig.Title, cancellationToken)
                .ConfigureAwait(false);
            return new PodcastTargets { LocalFilePath = localPath, FeedUrl = feedUrl };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve feed URL for footer; falling back to local-only");
            return new PodcastTargets { LocalFilePath = localPath, FeedUrl = null };
        }
    }

    public async Task<PodcastResult> GeneratePodcastAsync(
        Collection collection,
        IProgress<PodcastProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        _logger.LogInformation("Starting podcast generation for '{Collection}'", collection.Name);

        // Step 1: Pre-flight checks
        // workspace-pvr6: untyped failure — PodcastFailureClassifier's heuristic
        // matches "API key" / "not configured" and routes to the credentials
        // remediation copy without needing a typed FailureDetail here. Typing
        // would duplicate string-pattern-matching with no user-visible gain.
        if (!_ttsService.IsConfigured)
        {
            // Engine-neutral (workspace-2xej.10): the orchestrator has no engine
            // context, and the copy must be true for both OpenAI and the local
            // engine. The per-engine specifics live in the p-flow preflight.
            return PodcastResult.Failure("Narration isn't ready — press c (Settings) → Narration engine to finish setup.");
        }

        // workspace-pvr6: untyped — classifier's "FFmpeg" pattern produces the
        // brew/apt install remediation. No upside to typing.
        if (!await _audioAssembler.ValidatePrerequisitesAsync(cancellationToken).ConfigureAwait(false))
        {
            return PodcastResult.Failure("FFmpeg is not installed or not found in PATH.");
        }

        // workspace-hzjs (F.3): persist a Running PodcastJob row so generation
        // shows up in the orphan sweep (workspace-nk06) and the future resume
        // path (workspace-ur2s) can see it. Best-effort — never fails the run.
        PodcastJobJournal? journal = null;
        if (_scopeFactory is not null)
        {
            try
            {
                var targets = await ResolveTargetsAsync(collection, cancellationToken).ConfigureAwait(false);
                journal = await PodcastJobJournal.CreateAsync(
                    _scopeFactory,
                    collection,
                    targets.LocalFilePath,
                    targets.FeedUrl,
                    JobJournalDebounceInterval,
                    _logger,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to start podcast job journal — generation will proceed without DB persistence");
            }
        }

        PodcastProgress? lastSnapshot = null;
        IProgress<PodcastProgress>? wrappedProgress = progress;
        if (journal is not null)
        {
            var inner = progress;
            var j = journal;
            wrappedProgress = new SyncProgress<PodcastProgress>(p =>
            {
                lastSnapshot = p;
                j.ReportProgress(p);
                inner?.Report(p);
            });
        }

        PodcastResult result;
        try
        {
            result = await GeneratePodcastCoreAsync(collection, wrappedProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (journal is not null)
            {
                await journal.MarkCancelledAsync(lastSnapshot, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (journal is not null)
        {
            await journal.FinishAsync(result, lastSnapshot, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    public Task<CacheAnalysis> AnalyzeCacheStatusAsync(
        Collection collection,
        IProgress<ContentExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var task = AnalyzeCacheStatusCoreAsync(collection, progress, cancellationToken);

        // Store the running task so GeneratePodcastAsync can await it if the user
        // skips the analysis UI before extraction completes.
        _pendingAnalysisTask = task;
        _pendingAnalysisCollection = collection.Name;

        return task;
    }

    /// <summary>
    /// Builds the full text to send to TTS, prepending headline, author, and date
    /// as a spoken introduction before the article body.
    /// </summary>
    internal static string BuildSpokenText(ExtractedArticle article)
    {
        var parts = new List<string> { article.Title + "." };

        if (!string.IsNullOrWhiteSpace(article.Author))
        {
            parts.Add($"By {article.Author}.");
        }

        if (article.PublishedDate.HasValue)
        {
            parts.Add($"Published {article.PublishedDate.Value:MMMM d, yyyy}.");
        }

        parts.Add(string.Empty); // blank line separator
        parts.Add(article.CleanedText);

        return string.Join("\n", parts);
    }

    private static string SanitizeFileName(string name) => FileNameSanitizer.Sanitize(name);

    /// <summary>
    /// workspace-yg9l: locate the workspace-vendored podcast cover PNG so
    /// the M4B and (later) the RSS feed can advertise consistent cover art.
    /// Returns null when the asset can't be found rather than failing the
    /// run — the M4B assembler tolerates a missing CoverArtPath.
    /// </summary>
    private static string? ResolveCoverArtPath()
    {
        try
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "assets", "podcast-cover.png");
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ExpandUserPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    /// <summary>
    /// Maps a typed <see cref="FeedPublishFailureClass"/> to the single-line
    /// remediation copy rendered on the Phase E result screen (workspace-3a2k).
    /// Targeted enough to be actionable (bucket-public IAM grant, audio-file
    /// triage) without leaning on the heuristic string-pattern fallback in
    /// <c>PodcastFailureClassifier</c>.
    /// </summary>
    private static string BuildPublishRemediation(FeedPublishFailureClass failureClass) => failureClass switch
    {
        FeedPublishFailureClass.BucketNotPublic =>
            "feed.xml uploaded but the bucket isn't world-readable. Grant allUsers:objectViewer on the bucket "
            + "(Cloud Console → Buckets → Permissions → Add: allUsers, role Storage Object Viewer), or run "
            + "`gsutil iam ch allUsers:objectViewer gs://<your-bucket>`.",
        FeedPublishFailureClass.FeedNotReachable =>
            "feed.xml uploaded but the public URL is not reachable (non-403 response). Check the bucket name "
            + "and network connectivity, then retry. If this persists, see logs at ~/.local/share/WireCopy/logs/.",
        FeedPublishFailureClass.FeedNotParseable =>
            "feed.xml uploaded but the response body did not parse as XML. Re-run with the same key — this usually "
            + "indicates an encoding/transfer issue in the just-uploaded blob.",
        FeedPublishFailureClass.NoAudioFiles =>
            "Every episode skipped because the local audio file was missing on disk. Check ffmpeg output / "
            + "temp-dir cleanup and re-run.",
        _ =>
            "Check the bucket name + service-account key in Setup, or see logs at ~/.local/share/WireCopy/logs/ for the underlying error.",
    };

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var wordCount = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                wordCount++;
            }
        }

        return wordCount;
    }

    private async Task<PodcastResult> GeneratePodcastCoreAsync(
        Collection collection,
        IProgress<PodcastProgress>? progress,
        CancellationToken cancellationToken)
    {
        // All pipeline state declared up-front so catch/finally blocks can access them
        IReadOnlyList<ArticleFailure> extractionFailures = [];
        var segments = new List<ArticleAudioSegment>();
        var ttsFailures = new List<ArticleFailure>();
        TempFileManager? tempFiles = null;
        var keepTempDir = false;

        try
        {
            // Step 2: Extract article content (reuse cached articles from AnalyzeCacheStatusAsync if available)
            // If the user skipped the analysis UI, the extraction may still be running.
            // Await it to avoid redundant re-extraction.
            await AwaitPendingAnalysisAsync(collection.Name, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<ExtractedArticle> articles;
            if (_cachedArticles != null && _cachedCollectionName == collection.Name)
            {
                articles = _cachedArticles;
                extractionFailures = _cachedExtractionFailures ?? [];
                _cachedArticles = null;
                _cachedExtractionFailures = null;
                _cachedCollectionName = null;

                _logger.LogInformation(
                    "Reusing {Count} articles from cache analysis phase", articles.Count);

                progress?.Report(new PodcastProgress
                {
                    Phase = PodcastPhase.CachingContent,
                    Message = "Articles loaded (cached from analysis)",
                    PercentComplete = 10,
                });
            }
            else
            {
                progress?.Report(new PodcastProgress
                {
                    Phase = PodcastPhase.CachingContent,
                    Message = "Loading articles...",
                    PercentComplete = 0,
                });

                articles = await _contentProvider.GetAllArticleContentAsync(
                    collection,
                    new Progress<ContentExtractionProgress>(p =>
                        progress?.Report(new PodcastProgress
                        {
                            Phase = PodcastPhase.CachingContent,
                            CurrentArticle = p.Current,
                            TotalArticles = p.Total,
                            ArticleTitle = p.Title,
                            ExtractionMethod = p.ExtractionMethod,
                            IsArticleComplete = p.IsCompleted,
                            IsArticleSuccess = p.IsSuccess,
                            PercentComplete = (int)(p.Current * 10.0 / p.Total),
                        })),
                    cancellationToken).ConfigureAwait(false);

                extractionFailures = _contentProvider.LastExtractionFailures;
            }

            if (articles.Count == 0)
            {
                // workspace-pvr6: untyped — classifier's "No readable articles"
                // pattern routes to the "open articles in browser first" copy.
                return PodcastResult.Failure(
                    "No readable articles found in the collection.",
                    failedArticleDetails: extractionFailures);
            }

            // Step 2b: Content quality validation — skip articles that are too short or lack a title
            var qualityFailures = new List<ArticleFailure>();
            var qualityArticles = new List<ExtractedArticle>();

            foreach (var article in articles)
            {
                if (string.IsNullOrWhiteSpace(article.Title))
                {
                    _logger.LogWarning(
                        "Skipping article '{Url}': empty title",
                        article.Url);
                    qualityFailures.Add(new ArticleFailure
                    {
                        Title = article.Title ?? "(no title)",
                        Url = article.Url,
                        Reason = "Article has no title",
                    });
                    continue;
                }

                var wordCount = article.WordCount > 0
                    ? article.WordCount
                    : CountWords(article.CleanedText);

                if (wordCount < _podcastConfig.MinimumWordCount)
                {
                    _logger.LogWarning(
                        "Skipping article '{Title}': only {WordCount} words (minimum {Min})",
                        article.Title,
                        wordCount,
                        _podcastConfig.MinimumWordCount);
                    qualityFailures.Add(new ArticleFailure
                    {
                        Title = article.Title,
                        Url = article.Url,
                        Reason = $"Content too short ({wordCount} words, minimum {_podcastConfig.MinimumWordCount})",
                    });
                    continue;
                }

                qualityArticles.Add(article);
            }

            extractionFailures = extractionFailures.Concat(qualityFailures).ToList();
            articles = qualityArticles;

            if (articles.Count == 0)
            {
                // workspace-pvr6: untyped — the heuristic falls through to the
                // generic "see logs" remediation, which is correct: the quality
                // failures live in failedArticleDetails and the result screen
                // renders them inline. No typed FailureClass adds value here.
                return PodcastResult.Failure(
                    "All articles failed content quality validation.",
                    failedArticleDetails: extractionFailures);
            }

            if (qualityFailures.Count > 0)
            {
                _logger.LogInformation(
                    "Content quality gate: {Passed} articles passed, {Failed} skipped",
                    articles.Count,
                    qualityFailures.Count);
            }

            // Step 3: Estimate cost (accounting for cached articles) and check budget
            var cacheAnalysis = await _audioCache.AnalyzeCollectionAsync(
                articles.Select(a => (a.Url, a.Title, BuildSpokenText(a))).ToList(),
                cancellationToken).ConfigureAwait(false);

            var totalCost = cacheAnalysis.EstimatedCost;

            if (totalCost > _ttsConfig.MaxBudgetUsd)
            {
                // workspace-pvr6: untyped — classifier matches "budget"/"cost"
                // and routes to the cost-gate remediation.
                return PodcastResult.Failure(
                    $"Estimated cost ${totalCost:F4} exceeds budget ${_ttsConfig.MaxBudgetUsd:F2}. " +
                    $"Reduce articles or increase MaxBudgetUsd.",
                    failedArticleDetails: extractionFailures);
            }

            _logger.LogInformation(
                "Estimated TTS cost: ${Cost:F4} for {Count} articles ({Cached} cached, {Uncached} need generation)",
                totalCost,
                articles.Count,
                cacheAnalysis.CachedArticles,
                cacheAnalysis.UncachedArticles);

            // Step 4: Generate TTS audio for each article
            tempFiles = new TempFileManager(_podcastConfig.TempDirectory, _logger);

            // workspace-74zy: stopwatch per phase + cache-hit count emission so
            // a downstream weighted-ETA consumer can correctly weight
            // upload-bound vs. TTS-bound runs without inferring it from the
            // text itself.
            var audioPhaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
            progress?.Report(new PodcastProgress
            {
                Phase = PodcastPhase.GeneratingAudio,
                TotalArticles = articles.Count,
                PercentComplete = 10,
                CachedArticleCount = cacheAnalysis.CachedArticles,
                UncachedArticleCount = cacheAnalysis.UncachedArticles,
                Message = $"{cacheAnalysis.CachedArticles} cached · {cacheAnalysis.UncachedArticles} need TTS",
                PhaseElapsed = audioPhaseStopwatch.Elapsed,
            });

            for (var i = 0; i < articles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var article = articles[i];
                progress?.Report(new PodcastProgress
                {
                    Phase = PodcastPhase.GeneratingAudio,
                    CurrentArticle = i + 1,
                    TotalArticles = articles.Count,
                    ArticleTitle = article.Title,
                    PercentComplete = 10 + (int)((i + 1) * 60.0 / articles.Count),
                    CachedArticleCount = cacheAnalysis.CachedArticles,
                    UncachedArticleCount = cacheAnalysis.UncachedArticles,
                    PhaseElapsed = audioPhaseStopwatch.Elapsed,
                });

                // Build spoken text with headline/author/date intro
                var spokenText = BuildSpokenText(article);

                // Check cache first
                var cached = await _audioCache.TryGetAsync(spokenText, article.Url, cancellationToken).ConfigureAwait(false);
                string audioPath;

                if (cached != null)
                {
                    // Cache hit — use cached audio file directly
                    audioPath = cached.AudioFilePath;
                    progress?.Report(new PodcastProgress
                    {
                        Phase = PodcastPhase.GeneratingAudio,
                        CurrentArticle = i + 1,
                        TotalArticles = articles.Count,
                        ArticleTitle = article.Title,
                        PercentComplete = 10 + (int)((i + 1) * 60.0 / articles.Count),
                        IsFromCache = true,
                        CachedArticleCount = cacheAnalysis.CachedArticles,
                        UncachedArticleCount = cacheAnalysis.UncachedArticles,
                        PhaseElapsed = audioPhaseStopwatch.Elapsed,
                    });
                    _logger.LogInformation(
                        "Using cached audio for '{Title}' (key={Key})",
                        article.Title,
                        cached.CacheKey);
                }
                else
                {
                    // workspace-74zy: forward TTS per-chunk progress through to
                    // the podcast progress sink. Previously OpenAiTtsService
                    // emitted TtsProgress per chunk but the orchestrator never
                    // subscribed, so multi-chunk articles went silent during
                    // synth. Now consumers see chunk index + percent for the
                    // current article in real time.
                    IProgress<TtsProgress>? ttsProgress = null;
                    if (progress is not null)
                    {
                        var outerProgress = progress;
                        var articleIndex = i + 1;
                        var totalArticles = articles.Count;
                        var title = article.Title;
                        var stopwatch = audioPhaseStopwatch;
                        var cachedSnapshot = cacheAnalysis.CachedArticles;
                        var uncachedSnapshot = cacheAnalysis.UncachedArticles;
                        ttsProgress = new SyncProgress<TtsProgress>(p => outerProgress.Report(new PodcastProgress
                        {
                            Phase = PodcastPhase.GeneratingAudio,
                            CurrentArticle = articleIndex,
                            TotalArticles = totalArticles,
                            ArticleTitle = title,
                            PercentComplete = 10 + (int)((articleIndex - 1 + (p.PercentComplete / 100.0)) * 60.0 / totalArticles),
                            CurrentArticleChunkIndex = p.CurrentChunk,
                            CurrentArticleChunkTotal = p.TotalChunks,
                            CurrentArticleChunkPercent = p.PercentComplete,
                            CachedArticleCount = cachedSnapshot,
                            UncachedArticleCount = uncachedSnapshot,
                            PhaseElapsed = stopwatch.Elapsed,
                            Message = p.Message,

                            // workspace-rz1c: carry retry state through so the
                            // screen can render a "rate-limited, retrying" banner.
                            IsRetrying = p.IsRetrying,
                            RetryAttempt = p.RetryAttempt,
                            RetryMaxAttempts = p.RetryMaxAttempts,
                            RetryDelaySeconds = p.RetryDelaySeconds,
                        }));
                    }

                    var ttsResult = await _ttsService.GenerateAudioAsync(
                        spokenText,
                        article.Title,
                        ttsProgress,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (!ttsResult.Success || ttsResult.AudioData == null)
                    {
                        _logger.LogWarning(
                            "TTS failed for '{Title}': {Error}",
                            article.Title,
                            ttsResult.ErrorMessage);
                        ttsFailures.Add(new ArticleFailure
                        {
                            Title = article.Title,
                            Url = article.Url,
                            Reason = ttsResult.ErrorMessage ?? "TTS generation failed",
                        });
                        continue;
                    }

                    // Store in cache
                    var cacheEntry = await _audioCache.PutAsync(
                        spokenText,
                        article.Url,
                        article.Title,
                        ttsResult.AudioData,
                        cancellationToken).ConfigureAwait(false);
                    audioPath = cacheEntry.AudioFilePath;

                    _logger.LogInformation(
                        "Generated and cached audio for '{Title}' ({Chars} chars)",
                        article.Title,
                        ttsResult.CharactersProcessed);
                }

                // Duration left as TimeSpan.Zero so M4bAudioAssembler probes
                // the actual audio file with FFProbe for accurate chapter markers.
                segments.Add(new ArticleAudioSegment
                {
                    Title = article.Title,
                    AudioFilePath = audioPath,
                    Duration = TimeSpan.Zero,
                    SourceUrl = article.Url,
                });
            }

            var allFailures = extractionFailures.Concat(ttsFailures).ToList();

            if (segments.Count == 0)
            {
                // workspace-pvr6: untyped — the per-article failure reasons in
                // allFailures carry the typed 429/401/500 details from the TTS
                // service. The result screen surfaces them inline; the
                // classifier's TTS-error patterns produce a sensible Step/Fix
                // line. No typed FailureDetail adds clarity here.
                return PodcastResult.Failure(
                    "All articles failed TTS generation.",
                    failedArticleDetails: allFailures);
            }

            // Step 5: Assemble M4B
            progress?.Report(new PodcastProgress
            {
                Phase = PodcastPhase.AssemblingAudio,
                PercentComplete = 70,
                Message = "Assembling M4B audiobook...",
            });

            // Final M4B lands in the persistent output folder, not the per-run temp dir,
            // so the user can reliably find it and the path on the success screen
            // points at a file that survives cleanup.
            var outputPath = GetOutputFilePath(collection.Name);
            var assemblyRequest = new AssemblyRequest
            {
                Segments = segments,
                OutputPath = outputPath,
                Metadata = new AudioMetadata
                {
                    Title = $"{_podcastConfig.Title}: {collection.Name}",
                    Author = _podcastConfig.Author,
                    Description = _podcastConfig.Description,
                    Genre = "Podcast",

                    // workspace-yg9l: embed the WIRE COPY cover art into the
                    // M4B's user-data box so macOS Music / Apple Podcasts /
                    // VLC render the wordmark as album art. Path resolves
                    // relative to the running binary (Content item in
                    // WireCopy.API.csproj copies assets/ to the bin dir).
                    CoverArtPath = ResolveCoverArtPath(),
                },
            };

            // workspace-74zy: stopwatch + assembly progress forwarding. The
            // assembler ticks once per segment probe + a steady stream from
            // FFmpeg's NotifyOnProgress callback during concat. We translate
            // both into PodcastProgress events so the consumer can show a
            // moving bar through the assembly phase.
            var assemblyPhaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
            IProgress<AssemblyProgress>? assemblyProgress = null;
            if (progress is not null)
            {
                var outerProgress = progress;
                var stopwatch = assemblyPhaseStopwatch;
                assemblyProgress = new SyncProgress<AssemblyProgress>(p => outerProgress.Report(new PodcastProgress
                {
                    Phase = PodcastPhase.AssemblingAudio,
                    PercentComplete = 70 + (int)(p.FfmpegPercent * 0.15),
                    AssembledSegments = p.CompletedSegments,
                    AssembledSegmentsTotal = p.TotalSegments,
                    Message = p.Message,
                    PhaseElapsed = stopwatch.Elapsed,
                }));
            }

            var assemblyResult = await _audioAssembler.AssembleAsync(assemblyRequest, assemblyProgress, cancellationToken).ConfigureAwait(false);
            if (!assemblyResult.Success || string.IsNullOrEmpty(assemblyResult.OutputPath))
            {
                // workspace-pvr6: untyped — assembly errors are typically
                // FFmpeg-related (caught by the classifier's "ffmpeg" pattern)
                // or transient I/O issues that don't have a FailureClass.
                // The error message is rich enough for the heuristic.
                return PodcastResult.Failure(
                    $"M4B assembly failed: {assemblyResult.ErrorMessage}",
                    failedArticleDetails: allFailures);
            }

            // Step 6: Publish
            progress?.Report(new PodcastProgress
            {
                Phase = PodcastPhase.Publishing,
                PercentComplete = 85,
                Message = "Publishing feed...",
            });

            var podcastMetadata = new PodcastMetadata
            {
                Title = _podcastConfig.Title,
                Description = _podcastConfig.Description,
                Author = _podcastConfig.Author,
                Language = _podcastConfig.Language,
                ImageUrl = _podcastConfig.ImageUrl ?? string.Empty,
                Category = _podcastConfig.Category,
                Explicit = _podcastConfig.Explicit,
            };

            var episodeSources = new List<EpisodeSource>
            {
                new()
                {
                    Title = $"{collection.Name} ({DateTime.UtcNow:yyyy-MM-dd})",
                    Description = $"Articles from the '{collection.Name}' reading list.",
                    LocalAudioFilePath = assemblyResult.OutputPath,
                    Duration = assemblyResult.TotalDuration,
                    Chapters = assemblyResult.Chapters
                        .Select(c => new ChapterMark
                        {
                            Title = c.Title,
                            StartTime = c.StartTime,
                        })
                        .ToList(),
                },
            };

            // workspace-74zy: forward publish progress through to PodcastProgress
            // so the consumer can show "N of M episodes uploaded" + bytes/total
            // for the in-flight episode instead of holding the bar at 85% for
            // the full duration of a multi-MB upload.
            var publishPhaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
            IProgress<PublishProgress>? publishProgress = null;
            if (progress is not null)
            {
                var outerProgress = progress;
                var stopwatch = publishPhaseStopwatch;
                publishProgress = new SyncProgress<PublishProgress>(p =>
                {
                    var per = p.TotalEpisodes > 0
                        ? (double)p.UploadedEpisodes / p.TotalEpisodes
                        : 0;
                    var inflight = p.UploadedBytesTotal > 0
                        ? (double)p.UploadedBytes / p.UploadedBytesTotal
                        : 0;
                    var fraction = p.TotalEpisodes > 0
                        ? Math.Clamp(per + (inflight / p.TotalEpisodes), 0, 1)
                        : 0;
                    outerProgress.Report(new PodcastProgress
                    {
                        Phase = PodcastPhase.Publishing,
                        PercentComplete = 85 + (int)(fraction * 14),
                        UploadedEpisodes = p.UploadedEpisodes,
                        UploadedEpisodesTotal = p.TotalEpisodes,
                        UploadedBytes = p.UploadedBytes,
                        UploadedBytesTotal = p.UploadedBytesTotal,
                        Message = p.Message,
                        PhaseElapsed = stopwatch.Elapsed,
                    });
                });
            }

            var publishResult = await _publisher.PublishFeedAsync(
                podcastMetadata,
                episodeSources,
                publishProgress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new PodcastProgress
            {
                Phase = PodcastPhase.Publishing,
                PercentComplete = 100,
                Message = publishResult.Success ? "Done!" : "Published locally only.",
            });

            // Keep temp dir so the M4B output survives dispose
            keepTempDir = true;

            if (!publishResult.Success)
            {
                _logger.LogWarning(
                    "Feed publishing failed: {Error}. M4B saved locally at {Path}",
                    publishResult.ErrorMessage,
                    assemblyResult.OutputPath);

                // workspace-3a2k: when the user configured a GCS bucket, a
                // publish failure is a TOTAL failure — not a "local-only
                // success". Otherwise the user sees "✓ Podcast Ready!" and
                // only finds the broken subscription URL when their podcast
                // app silently fails to add the feed. Carry the publisher's
                // typed FailureClass through so the result screen can render
                // the correct bucket-public remediation.
                var hadBucketConfigured = !string.IsNullOrWhiteSpace(_settingsStore?.Get("GcsBucketName"));
                if (hadBucketConfigured)
                {
                    var failureDetail = new PodcastFailureDetail(
                        Step: "Publishing",
                        FailureClass: publishResult.FailureClass,
                        RawMessage: publishResult.ErrorMessage ?? "Publish failed",
                        RemediationCopy: BuildPublishRemediation(publishResult.FailureClass));

                    return PodcastResult.Failure(
                        publishResult.ErrorMessage ?? "Publish failed",
                        detail: failureDetail,
                        localFilePath: assemblyResult.OutputPath,
                        failedArticleDetails: allFailures);
                }

                return PodcastResult.Successful(
                    feedUrl: null,
                    localFilePath: assemblyResult.OutputPath,
                    totalDuration: assemblyResult.TotalDuration,
                    articlesProcessed: segments.Count,
                    articlesFailed: allFailures.Count,
                    fileSizeBytes: assemblyResult.FileSizeBytes,
                    articlesCached: cacheAnalysis.CachedArticles,
                    totalCost: totalCost,
                    failedArticleDetails: allFailures);
            }

            return PodcastResult.Successful(
                feedUrl: publishResult.FeedUrl,
                localFilePath: assemblyResult.OutputPath,
                totalDuration: assemblyResult.TotalDuration,
                articlesProcessed: segments.Count,
                articlesFailed: allFailures.Count,
                fileSizeBytes: assemblyResult.FileSizeBytes,
                articlesCached: cacheAnalysis.CachedArticles,
                totalCost: totalCost,
                failedArticleDetails: allFailures);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Podcast generation failed: {Message}", ex.Message);

            // workspace-pvr6: outer catch is the catch-all for unforeseen
            // exceptions. The exception's message string is the richest
            // signal we have, and the heuristic classifier handles common
            // patterns (network/auth/budget/ffmpeg) better than a hand-typed
            // FailureClass=Generic would. Leave untyped on purpose.
            return PodcastResult.Failure(
                $"Podcast generation failed: {ex.Message}",
                failedArticleDetails: extractionFailures.Concat(ttsFailures).ToList());
        }
        finally
        {
            if (!keepTempDir)
            {
                // On failure/cancellation, clean up temp directory
                tempFiles?.Dispose();
            }

            // Audio segment files are managed by the TTS cache — do not delete them here.
        }
    }

    /// <summary>
    /// Resolves the user-overridden output folder (from <see cref="IUserSettingsStore"/>)
    /// when set, otherwise falls back to <see cref="PodcastConfiguration.ResolveOutputFolderPath"/>.
    /// Tilde paths (~/...) are expanded so the user can paste a friendly path
    /// from the confirmation screen.
    /// </summary>
    private string ResolveEffectiveOutputFolder()
    {
        var saved = _settingsStore?.Get("PodcastOutputFolder");
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return ExpandUserPath(saved);
        }

        return _podcastConfig.ResolveOutputFolderPath();
    }

    private async Task<CacheAnalysis> AnalyzeCacheStatusCoreAsync(
        Collection collection,
        IProgress<ContentExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var articles = await _contentProvider.GetAllArticleContentAsync(
                collection,
                progress,
                cancellationToken).ConfigureAwait(false);

            // Cache for reuse by GeneratePodcastAsync to avoid double extraction
            _cachedArticles = articles;
            _cachedExtractionFailures = _contentProvider.LastExtractionFailures;
            _cachedCollectionName = collection.Name;

            if (articles.Count == 0)
            {
                return new CacheAnalysis
                {
                    TotalArticles = 0,
                    CachedArticles = 0,
                    UncachedArticles = 0,
                    EstimatedCost = 0,
                    ArticleStatuses = [],
                };
            }

            return await _audioCache.AnalyzeCollectionAsync(
                articles.Select(a => (a.Url, a.Title, BuildSpokenText(a))).ToList(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache analysis failed unexpectedly");
            return new CacheAnalysis
            {
                TotalArticles = 0,
                CachedArticles = 0,
                UncachedArticles = 0,
                EstimatedCost = 0,
                ArticleStatuses = [],
            };
        }
    }

    /// <summary>
    /// If AnalyzeCacheStatusAsync was started but the user skipped the UI before it finished,
    /// await the pending task so its extracted articles are available in _cachedArticles.
    /// </summary>
    private async Task AwaitPendingAnalysisAsync(string collectionName, CancellationToken cancellationToken)
    {
        var pendingTask = _pendingAnalysisTask;
        if (pendingTask == null || _pendingAnalysisCollection != collectionName)
        {
            return;
        }

        // Already completed and results were consumed — nothing to wait for
        if (pendingTask.IsCompleted && _cachedArticles != null)
        {
            return;
        }

        if (!pendingTask.IsCompleted)
        {
            _logger.LogInformation("Awaiting background analysis that was still running when user skipped");
            try
            {
                await pendingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Analysis failed — GeneratePodcastAsync will fall back to fresh extraction
                _logger.LogWarning(ex, "Pending analysis task failed, will re-extract");
            }
        }

        _pendingAnalysisTask = null;
        _pendingAnalysisCollection = null;
    }
}
