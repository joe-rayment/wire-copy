// <copyright file="AudioGeneratorTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NYTAudioScraper.Infrastructure.Audio;
using NYTAudioScraper.Infrastructure.Configuration;
using Xunit;

namespace NYTAudioScraper.Tests;

public class AudioGeneratorTests
{
    private readonly ILogger<AudioGenerator> _logger;
    private readonly IOptions<ElevenLabsConfiguration> _config;

    public AudioGeneratorTests()
    {
        _logger = Substitute.For<ILogger<AudioGenerator>>();
        _config = Options.Create(new ElevenLabsConfiguration
        {
            ApiKey = "test-api-key",
            BaseUrl = "https://api.elevenlabs.io",
            Model = "eleven_multilingual_v2",
            VoiceId = "test-voice-id",
            CostPerCharacter = 0.0003m
        });
    }

    [Fact]
    public void EstimateCost_WithShortText_ReturnsCorrectEstimate()
    {
        // Arrange
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var generator = new AudioGenerator(_config, _logger, httpClientFactory);
        var text = "Hello, world!"; // 13 characters

        // Act
        var cost = generator.EstimateCost(text);

        // Assert
        cost.Should().Be(0.0003m * 13); // 0.0039
    }

    [Fact]
    public void EstimateCost_WithLongText_ReturnsCorrectEstimate()
    {
        // Arrange
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var generator = new AudioGenerator(_config, _logger, httpClientFactory);
        var text = new string('a', 1000);

        // Act
        var cost = generator.EstimateCost(text);

        // Assert
        cost.Should().Be(0.0003m * 1000); // 0.30
    }

    [Fact]
    public void EstimateCost_WithEmptyText_ReturnsZero()
    {
        // Arrange
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var generator = new AudioGenerator(_config, _logger, httpClientFactory);
        var text = "";

        // Act
        var cost = generator.EstimateCost(text);

        // Assert
        cost.Should().Be(0);
    }

    [Theory]
    [InlineData(100, 0.03)]
    [InlineData(500, 0.15)]
    [InlineData(1000, 0.30)]
    [InlineData(5000, 1.50)]
    public void EstimateCost_VariousTextLengths_CalculatesCorrectly(int characterCount, decimal expectedCost)
    {
        // Arrange
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var generator = new AudioGenerator(_config, _logger, httpClientFactory);
        var text = new string('a', characterCount);

        // Act
        var cost = generator.EstimateCost(text);

        // Assert
        cost.Should().Be(expectedCost);
    }

    [Fact]
    public void EstimateCost_WithDifferentCostPerCharacter_UsesConfigValue()
    {
        // Arrange
        var customConfig = Options.Create(new ElevenLabsConfiguration
        {
            ApiKey = "test-api-key",
            BaseUrl = "https://api.elevenlabs.io",
            Model = "test-model",
            VoiceId = "test-voice",
            CostPerCharacter = 0.0005m // Different cost
        });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var generator = new AudioGenerator(customConfig, _logger, httpClientFactory);
        var text = new string('a', 1000);

        // Act
        var cost = generator.EstimateCost(text);

        // Assert
        cost.Should().Be(0.0005m * 1000); // 0.50
    }

    [Fact]
    public void Constructor_WithMissingApiKey_LogsWarning()
    {
        // Arrange
        var configWithoutKey = Options.Create(new ElevenLabsConfiguration
        {
            ApiKey = "",
            BaseUrl = "https://api.elevenlabs.io",
            Model = "test-model",
            VoiceId = "test-voice",
            CostPerCharacter = 0.0003m
        });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();

        // Act
        var generator = new AudioGenerator(configWithoutKey, _logger, httpClientFactory);

        // Assert
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("API key is not configured")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void EstimateCost_WithUnicodeCharacters_CountsCharactersCorrectly()
    {
        // Arrange
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var generator = new AudioGenerator(_config, _logger, httpClientFactory);
        var text = "Hello 世界! 🌍"; // Mixed ASCII, Chinese, and emoji

        // Act
        var cost = generator.EstimateCost(text);

        // Assert
        cost.Should().Be(0.0003m * text.Length);
    }

    [Fact]
    public void EstimateCost_WithNewlinesAndWhitespace_IncludesAllCharacters()
    {
        // Arrange
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var generator = new AudioGenerator(_config, _logger, httpClientFactory);
        var text = "Line 1\n\nLine 2\t\tLine 3";

        // Act
        var cost = generator.EstimateCost(text);

        // Assert
        cost.Should().Be(0.0003m * text.Length);
    }
}
