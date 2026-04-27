// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs;
using TermReader.Application.DTOs.Audio;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Audio;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.ValueObjects.Audio;
using TermReader.Domain.ValueObjects.Podcast;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Storage;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Orchestrates the end-to-end podcast generation pipeline:
/// content extraction, TTS generation, M4B assembly, and feed publishing.
/// </summary>
internal sealed class PodcastOrchestrator : IPodcastOrchestrator
{
    private readonly ReadingListContentProvider _contentProvider;
    private readonly ITtsService _ttsService;
    private readonly ITtsAudioCache _audioCache;
    private readonly IAudioAssembler _audioAssembler;
    private readonly IPodcastPublisher _publisher;
    private readonly PodcastConfiguration _podcastConfig;
    private readonly OpenAiTtsConfiguration _ttsConfig;
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
        ILogger<PodcastOrchestrator> logger)
    {
        _contentProvider = contentProvider;
        _ttsService = ttsService;
        _audioCache = audioCache;
        _audioAssembler = audioAssembler;
        _publisher = publisher;
        _podcastConfig = podcastConfig.Value;
        _ttsConfig = ttsConfig.Value;
        _logger = logger;
    }

    public async Task<PodcastResult> GeneratePodcastAsync(
        Collection collection,
        IProgress<PodcastProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        _logger.LogInformation("Starting podcast generation for '{Collection}'", collection.Name);

        // Step 1: Pre-flight checks
        if (!_ttsService.IsConfigured)
        {
            return PodcastResult.Failure("TTS service is not configured. Set an OpenAI API key.");
        }

        if (!await _audioAssembler.ValidatePrerequisitesAsync(cancellationToken).ConfigureAwait(false))
        {
            return PodcastResult.Failure("FFmpeg is not installed or not found in PATH.");
        }

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
                    });
                    _logger.LogInformation(
                        "Using cached audio for '{Title}' (key={Key})",
                        article.Title,
                        cached.CacheKey);
                }
                else
                {
                    // Cache miss — generate via TTS API
                    var ttsResult = await _ttsService.GenerateAudioAsync(
                        spokenText,
                        article.Title,
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

            var outputPath = tempFiles.GetTempFilePath($"{SanitizeFileName(collection.Name)}.m4b");
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
                },
            };

            var assemblyResult = await _audioAssembler.AssembleAsync(assemblyRequest, cancellationToken).ConfigureAwait(false);
            if (!assemblyResult.Success || string.IsNullOrEmpty(assemblyResult.OutputPath))
            {
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

            var publishResult = await _publisher.PublishFeedAsync(
                podcastMetadata,
                episodeSources,
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
