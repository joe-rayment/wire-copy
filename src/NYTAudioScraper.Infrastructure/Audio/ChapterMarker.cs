// <copyright file="ChapterMarker.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using ATL;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Audio;

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
        try
        {
            var chapterList = chapters.ToList();
            _logger.LogInformation("Adding {ChapterCount} chapters to {FilePath}", chapterList.Count, audioFilePath);

            if (!File.Exists(audioFilePath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
            }

            var track = new Track(audioFilePath);

            // Clear existing chapters
            track.Chapters.Clear();

            // Add new chapters
            foreach (var chapter in chapterList.OrderBy(c => c.StartTimeMs))
            {
                var atlChapter = new ChapterInfo
                {
                    StartTime = (uint)chapter.StartTimeMs,
                    EndTime = (uint)chapter.EndTimeMs,
                    Title = chapter.Title
                };

                track.Chapters.Add(atlChapter);
                _logger.LogDebug(
                    "Added chapter: {Title} ({Start}ms - {End}ms)",
                    chapter.Title,
                    chapter.StartTimeMs,
                    chapter.EndTimeMs);
            }

            // Save the changes
            var result = track.Save();

            if (result)
            {
                _logger.LogInformation(
                    "Successfully added {ChapterCount} chapters to {FilePath}",
                    chapterList.Count,
                    audioFilePath);
            }
            else
            {
                _logger.LogWarning("Failed to save chapters to {FilePath}", audioFilePath);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding chapters to {FilePath}", audioFilePath);
            throw;
        }
    }
}
