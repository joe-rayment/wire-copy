// <copyright file="ChaptersJsonGenerator.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Generates Podcasting 2.0 chapters JSON files.
/// See: https://github.com/Podcastindex-org/podcast-namespace/blob/main/chapters/jsonChapters.md
/// </summary>
public class ChaptersJsonGenerator : IChaptersJsonGenerator
{
    private const string ChaptersVersion = "1.2.0";

    private readonly ILogger<ChaptersJsonGenerator> _logger;

    public ChaptersJsonGenerator(ILogger<ChaptersJsonGenerator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string GenerateJson(IEnumerable<AudioChapter> chapters)
    {
        var chapterList = chapters.ToList();

        // Sort chapters by start time to ensure correct order
        var orderedChapters = chapterList.OrderBy(c => c.StartTimeMs).ToList();

        var chaptersObject = new ChaptersDocument
        {
            Version = ChaptersVersion,
            Chapters = orderedChapters.Select(c => new ChapterEntry
            {
                StartTime = c.StartTimeMs / 1000.0, // Convert ms to seconds
                Title = TruncateTitle(c.Title)
            }).ToArray()
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(chaptersObject, options);

        _logger.LogInformation("Generated chapters JSON with {Count} chapters", orderedChapters.Count);

        return json;
    }

    /// <inheritdoc/>
    public async Task SaveJsonAsync(IEnumerable<AudioChapter> chapters, string outputPath)
    {
        var json = GenerateJson(chapters);
        await File.WriteAllTextAsync(outputPath, json);
        _logger.LogInformation("Saved chapters JSON to: {Path}", outputPath);
    }

    /// <summary>
    /// Truncates chapter title to Apple's recommended 45 character limit.
    /// </summary>
    private static string TruncateTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return title;
        }

        // Apple recommends max 45 characters for chapter titles
        const int maxLength = 45;

        if (title.Length <= maxLength)
        {
            return title;
        }

        // Truncate and add ellipsis
        return title[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Represents the root chapters document.
    /// </summary>
    private sealed class ChaptersDocument
    {
        public required string Version { get; init; }

        public required ChapterEntry[] Chapters { get; init; }
    }

    /// <summary>
    /// Represents a single chapter entry.
    /// </summary>
    private sealed class ChapterEntry
    {
        public required double StartTime { get; init; }

        public required string Title { get; init; }
    }
}
