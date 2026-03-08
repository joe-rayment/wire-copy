// Educational and personal use only.

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

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Orchestrates the end-to-end podcast generation pipeline:
/// content extraction, TTS generation, M4B assembly, and feed publishing.
/// </summary>
internal sealed class PodcastOrchestrator : IPodcastOrchestrator
{
    private readonly ReadingListContentProvider _contentProvider;
    private readonly ITtsService _ttsService;
    private readonly IAudioAssembler _audioAssembler;
    private readonly IPodcastPublisher _publisher;
    private readonly PodcastConfiguration _podcastConfig;
    private readonly OpenAiTtsConfiguration _ttsConfig;
    private readonly ILogger<PodcastOrchestrator> _logger;

    public PodcastOrchestrator(
        ReadingListContentProvider contentProvider,
        ITtsService ttsService,
        IAudioAssembler audioAssembler,
        IPodcastPublisher publisher,
        IOptions<PodcastConfiguration> podcastConfig,
        IOptions<OpenAiTtsConfiguration> ttsConfig,
        ILogger<PodcastOrchestrator> logger)
    {
        _contentProvider = contentProvider;
        _ttsService = ttsService;
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

        if (!await _audioAssembler.ValidatePrerequisitesAsync(cancellationToken))
        {
            return PodcastResult.Failure("FFmpeg is not installed or not found in PATH.");
        }

        // Step 2: Extract article content
        progress?.Report(new PodcastProgress
        {
            Phase = PodcastPhase.CachingContent,
            Message = "Loading articles...",
            PercentComplete = 0,
        });

        var articles = await _contentProvider.GetAllArticleContentAsync(
            collection,
            new Progress<(int Current, int Total, string Title)>(p =>
                progress?.Report(new PodcastProgress
                {
                    Phase = PodcastPhase.CachingContent,
                    CurrentArticle = p.Current,
                    TotalArticles = p.Total,
                    ArticleTitle = p.Title,
                    PercentComplete = (int)(p.Current * 10.0 / p.Total),
                })),
            cancellationToken);

        if (articles.Count == 0)
        {
            return PodcastResult.Failure("No readable articles found in the collection.");
        }

        // Step 3: Estimate cost and check budget
        var totalCost = 0m;
        foreach (var article in articles)
        {
            totalCost += _ttsService.EstimateCost(article.CleanedText).EstimatedCostUsd;
        }

        if (totalCost > _ttsConfig.MaxBudgetUsd)
        {
            return PodcastResult.Failure(
                $"Estimated cost ${totalCost:F4} exceeds budget ${_ttsConfig.MaxBudgetUsd:F2}. " +
                $"Reduce articles or increase MaxBudgetUsd.");
        }

        _logger.LogInformation(
            "Estimated TTS cost: ${Cost:F4} for {Count} articles",
            totalCost,
            articles.Count);

        // Step 4: Generate TTS audio for each article
        var tempDir = Path.Combine(
            _podcastConfig.TempDirectory ?? Path.GetTempPath(),
            $"termreader-podcast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var segments = new List<ArticleAudioSegment>();
        var articlesFailed = 0;
        var succeeded = false;

        try
        {
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

                var ttsResult = await _ttsService.GenerateAudioAsync(
                    article.CleanedText,
                    article.Title,
                    cancellationToken: cancellationToken);

                if (!ttsResult.Success || ttsResult.AudioData == null)
                {
                    _logger.LogWarning(
                        "TTS failed for '{Title}': {Error}",
                        article.Title,
                        ttsResult.ErrorMessage);
                    articlesFailed++;
                    continue;
                }

                // Save audio to temp file
                var audioPath = Path.Combine(tempDir, $"segment-{i:D3}.aac");
                await File.WriteAllBytesAsync(audioPath, ttsResult.AudioData, cancellationToken);

                // Estimate duration from character count (150 WPM, 5 chars/word)
                var estimatedDuration = TimeSpan.FromMinutes(
                    article.CleanedText.Length / (150.0 * 5.0));

                segments.Add(new ArticleAudioSegment
                {
                    Title = article.Title,
                    AudioFilePath = audioPath,
                    Duration = estimatedDuration,
                    SourceUrl = article.Url,
                });

                _logger.LogInformation(
                    "Generated audio for '{Title}' ({Chars} chars)",
                    article.Title,
                    ttsResult.CharactersProcessed);
            }

            if (segments.Count == 0)
            {
                return PodcastResult.Failure("All articles failed TTS generation.");
            }

            // Step 5: Assemble M4B
            progress?.Report(new PodcastProgress
            {
                Phase = PodcastPhase.AssemblingAudio,
                PercentComplete = 70,
                Message = "Assembling M4B audiobook...",
            });

            var outputPath = Path.Combine(tempDir, $"{SanitizeFileName(collection.Name)}.m4b");
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

            var assemblyResult = await _audioAssembler.AssembleAsync(assemblyRequest, cancellationToken);
            if (!assemblyResult.Success || string.IsNullOrEmpty(assemblyResult.OutputPath))
            {
                return PodcastResult.Failure(
                    $"M4B assembly failed: {assemblyResult.ErrorMessage}");
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
                cancellationToken);

            progress?.Report(new PodcastProgress
            {
                Phase = PodcastPhase.Publishing,
                PercentComplete = 100,
                Message = publishResult.Success ? "Done!" : "Published locally only.",
            });

            succeeded = true;

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
                    articlesFailed: articlesFailed,
                    fileSizeBytes: assemblyResult.FileSizeBytes);
            }

            return PodcastResult.Successful(
                feedUrl: publishResult.FeedUrl,
                localFilePath: assemblyResult.OutputPath,
                totalDuration: assemblyResult.TotalDuration,
                articlesProcessed: segments.Count,
                articlesFailed: articlesFailed,
                fileSizeBytes: assemblyResult.FileSizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Podcast generation failed: {Message}", ex.Message);
            return PodcastResult.Failure($"Podcast generation failed: {ex.Message}");
        }
        finally
        {
            // Clean up temp segment files; keep the M4B if pipeline succeeded
            CleanupTempSegments(tempDir, succeeded);
        }
    }

    private void CleanupTempSegments(string tempDir, bool keepOutputFiles)
    {
        try
        {
            if (!Directory.Exists(tempDir))
            {
                return;
            }

            if (!keepOutputFiles)
            {
                Directory.Delete(tempDir, recursive: true);
                _logger.LogDebug("Cleaned up temp directory: {TempDir}", tempDir);
                return;
            }

            // On success, delete only segment files but keep the M4B output
            foreach (var segmentFile in Directory.GetFiles(tempDir, "segment-*.aac"))
            {
                File.Delete(segmentFile);
            }

            _logger.LogDebug("Cleaned up temp segment files in: {TempDir}", tempDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp directory: {TempDir}", tempDir);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
