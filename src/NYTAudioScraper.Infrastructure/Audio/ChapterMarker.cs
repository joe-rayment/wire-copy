using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Stub implementation of IChapterMarker
/// </summary>
public class ChapterMarker : IChapterMarker
{
    private readonly ILogger<ChapterMarker> _logger;

    public ChapterMarker(ILogger<ChapterMarker> logger)
    {
        _logger = logger;
    }

    public Task AddChaptersAsync(
        string audioFilePath,
        IEnumerable<AudioChapter> chapters,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("AddChaptersAsync called for {FilePath} with {ChapterCount} chapters (stub implementation)",
            audioFilePath, chapters.Count());
        return Task.CompletedTask;
    }
}
