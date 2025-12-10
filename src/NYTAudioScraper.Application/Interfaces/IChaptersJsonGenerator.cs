// <copyright file="IChaptersJsonGenerator.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Service for generating Podcasting 2.0 chapters JSON files.
/// </summary>
public interface IChaptersJsonGenerator
{
    /// <summary>
    /// Generates a Podcasting 2.0 chapters JSON string.
    /// </summary>
    /// <param name="chapters">The chapters to include.</param>
    /// <returns>JSON string conforming to Podcasting 2.0 chapters spec.</returns>
    string GenerateJson(IEnumerable<AudioChapter> chapters);

    /// <summary>
    /// Saves chapters JSON to a file.
    /// </summary>
    /// <param name="chapters">The chapters to include.</param>
    /// <param name="outputPath">Path to save the JSON file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveJsonAsync(IEnumerable<AudioChapter> chapters, string outputPath);
}
