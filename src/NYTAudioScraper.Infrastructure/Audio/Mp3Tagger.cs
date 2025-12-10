// <copyright file="Mp3Tagger.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using TagLib;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Implementation of MP3 tagging using TagLibSharp.
/// </summary>
public class Mp3Tagger : IMp3Tagger
{
    private readonly ILogger<Mp3Tagger> _logger;

    public Mp3Tagger(ILogger<Mp3Tagger> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task TagFileAsync(string filePath, Mp3Metadata metadata)
    {
        if (!System.IO.File.Exists(filePath))
        {
            throw new FileNotFoundException($"MP3 file not found: {filePath}", filePath);
        }

        try
        {
            using var file = TagLib.File.Create(filePath);

            // Set ID3v2 tags
            file.Tag.Title = metadata.Title;
            file.Tag.Performers = new[] { metadata.Artist };
            file.Tag.Album = metadata.Album;
            file.Tag.Track = (uint)metadata.TrackNumber;
            file.Tag.Year = (uint)metadata.PublishDate.Year;
            file.Tag.Genres = new[] { "News" };
            file.Tag.Comment = $"Published: {metadata.PublishDate:yyyy-MM-dd}";

            file.Save();

            _logger.LogDebug(
                "Tagged MP3: {FilePath} - Track {Track}: {Title}",
                filePath,
                metadata.TrackNumber,
                metadata.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to tag MP3 file: {FilePath}", filePath);
            throw;
        }

        return Task.CompletedTask;
    }
}
