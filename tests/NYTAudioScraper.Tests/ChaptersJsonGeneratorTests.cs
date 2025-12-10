// <copyright file="ChaptersJsonGeneratorTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Podcast;
using Xunit;

namespace NYTAudioScraper.Tests;

public class ChaptersJsonGeneratorTests
{
    private readonly ChaptersJsonGenerator _generator;
    private readonly ILogger<ChaptersJsonGenerator> _logger;

    public ChaptersJsonGeneratorTests()
    {
        _logger = Substitute.For<ILogger<ChaptersJsonGenerator>>();
        _generator = new ChaptersJsonGenerator(_logger);
    }

    [Fact]
    public void GenerateJson_ProducesValidJson()
    {
        // Arrange
        var chapters = new List<AudioChapter>
        {
            new() { Title = "First Article", ArticleId = "1", StartTimeMs = 0, DurationMs = 60000 },
            new() { Title = "Second Article", ArticleId = "2", StartTimeMs = 60000, DurationMs = 120000 },
        };

        // Act
        var json = _generator.GenerateJson(chapters);

        // Assert - should be valid JSON
        var parseAction = () => JsonDocument.Parse(json);
        parseAction.Should().NotThrow("JSON should be valid");
    }

    [Fact]
    public void GenerateJson_HasCorrectVersion()
    {
        // Arrange
        var chapters = new List<AudioChapter>
        {
            new() { Title = "Test Article", ArticleId = "1", StartTimeMs = 0, DurationMs = 60000 },
        };

        // Act
        var json = _generator.GenerateJson(chapters);
        var doc = JsonDocument.Parse(json);

        // Assert
        doc.RootElement.GetProperty("version").GetString().Should().Be("1.2.0");
    }

    [Fact]
    public void GenerateJson_ChaptersInCorrectOrder()
    {
        // Arrange - intentionally out of order
        var chapters = new List<AudioChapter>
        {
            new() { Title = "Third Article", ArticleId = "3", StartTimeMs = 120000, DurationMs = 60000 },
            new() { Title = "First Article", ArticleId = "1", StartTimeMs = 0, DurationMs = 60000 },
            new() { Title = "Second Article", ArticleId = "2", StartTimeMs = 60000, DurationMs = 60000 },
        };

        // Act
        var json = _generator.GenerateJson(chapters);
        var doc = JsonDocument.Parse(json);
        var chaptersArray = doc.RootElement.GetProperty("chapters");

        // Assert - should be sorted by start time
        chaptersArray[0].GetProperty("title").GetString().Should().Be("First Article");
        chaptersArray[1].GetProperty("title").GetString().Should().Be("Second Article");
        chaptersArray[2].GetProperty("title").GetString().Should().Be("Third Article");
    }

    [Fact]
    public void GenerateJson_StartTimeInSeconds()
    {
        // Arrange
        var chapters = new List<AudioChapter>
        {
            new() { Title = "First", ArticleId = "1", StartTimeMs = 0, DurationMs = 60000 },
            new() { Title = "Second", ArticleId = "2", StartTimeMs = 90000, DurationMs = 60000 }, // 90 seconds
            new() { Title = "Third", ArticleId = "3", StartTimeMs = 180500, DurationMs = 60000 }, // 180.5 seconds
        };

        // Act
        var json = _generator.GenerateJson(chapters);
        var doc = JsonDocument.Parse(json);
        var chaptersArray = doc.RootElement.GetProperty("chapters");

        // Assert - startTime should be in seconds (not ms)
        chaptersArray[0].GetProperty("startTime").GetDouble().Should().Be(0);
        chaptersArray[1].GetProperty("startTime").GetDouble().Should().Be(90);
        chaptersArray[2].GetProperty("startTime").GetDouble().Should().Be(180.5);
    }

    [Fact]
    public void GenerateJson_TruncatesLongTitles()
    {
        // Arrange - Apple recommends max 45 characters
        var longTitle = "This is a very long article title that exceeds the recommended limit of forty-five characters";
        var chapters = new List<AudioChapter>
        {
            new() { Title = longTitle, ArticleId = "1", StartTimeMs = 0, DurationMs = 60000 },
        };

        // Act
        var json = _generator.GenerateJson(chapters);
        var doc = JsonDocument.Parse(json);
        var chaptersArray = doc.RootElement.GetProperty("chapters");
        var title = chaptersArray[0].GetProperty("title").GetString();

        // Assert
        title.Should().HaveLength(45);
        title.Should().EndWith("...");
    }

    [Fact]
    public void GenerateJson_DoesNotTruncateShortTitles()
    {
        // Arrange
        var shortTitle = "Short Title";
        var chapters = new List<AudioChapter>
        {
            new() { Title = shortTitle, ArticleId = "1", StartTimeMs = 0, DurationMs = 60000 },
        };

        // Act
        var json = _generator.GenerateJson(chapters);
        var doc = JsonDocument.Parse(json);
        var chaptersArray = doc.RootElement.GetProperty("chapters");
        var title = chaptersArray[0].GetProperty("title").GetString();

        // Assert
        title.Should().Be(shortTitle);
    }

    [Fact]
    public void GenerateJson_FirstArticleHasZeroStartTime()
    {
        // Arrange
        var chapters = new List<AudioChapter>
        {
            new() { Title = "First Article", ArticleId = "1", StartTimeMs = 0, DurationMs = 60000 },
            new() { Title = "Second Article", ArticleId = "2", StartTimeMs = 60000, DurationMs = 60000 },
        };

        // Act
        var json = _generator.GenerateJson(chapters);
        var doc = JsonDocument.Parse(json);
        var chaptersArray = doc.RootElement.GetProperty("chapters");

        // Assert
        chaptersArray[0].GetProperty("startTime").GetDouble().Should().Be(0);
    }

    [Fact]
    public void GenerateJson_EmptyList_ReturnsEmptyChaptersArray()
    {
        // Arrange
        var chapters = new List<AudioChapter>();

        // Act
        var json = _generator.GenerateJson(chapters);
        var doc = JsonDocument.Parse(json);
        var chaptersArray = doc.RootElement.GetProperty("chapters");

        // Assert
        chaptersArray.GetArrayLength().Should().Be(0);
    }
}
