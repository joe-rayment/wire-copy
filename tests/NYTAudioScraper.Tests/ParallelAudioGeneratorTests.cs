// <copyright file="ParallelAudioGeneratorTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Audio;
using Xunit;

namespace NYTAudioScraper.Tests;

/// <summary>
/// Tests for IParallelAudioGenerator interface and ParallelAudioGenerator implementation
/// </summary>
public class ParallelAudioGeneratorTests
{
    private readonly IParallelAudioGenerator _parallelGenerator;
    private readonly IAudioGenerator _mockAudioGenerator;
    private readonly IRateLimiter _mockRateLimiter;
    private readonly ILogger<ParallelAudioGenerator> _logger;

    public ParallelAudioGeneratorTests()
    {
        _mockAudioGenerator = Substitute.For<IAudioGenerator>();
        _mockRateLimiter = Substitute.For<IRateLimiter>();
        _logger = Substitute.For<ILogger<ParallelAudioGenerator>>();

        _parallelGenerator = new ParallelAudioGenerator(
            _mockAudioGenerator,
            _mockRateLimiter,
            _logger);
    }

    [Fact]
    public async Task GenerateAudioForArticlesAsync_WithNoArticles_ReturnsEmptyResult()
    {
        // Arrange
        var articles = new List<Article>();
        var voiceId = "test-voice";

        // Act
        var result = await _parallelGenerator.GenerateAudioForArticlesAsync(articles, voiceId);

        // Assert
        result.Should().NotBeNull();
        result.SuccessfulGenerations.Should().BeEmpty();
        result.FailedGenerations.Should().BeEmpty();
        result.TotalProcessed.Should().Be(0);
        result.AllSuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAudioForArticlesAsync_WithSuccessfulGeneration_ReturnsSuccessResult()
    {
        // Arrange
        var article = CreateTestArticle("article-1", "Test Article", "Test content");
        var articles = new List<Article> { article };
        var voiceId = "test-voice";
        var audioData = new byte[] { 1, 2, 3, 4 };

        _mockRateLimiter.ExecuteAsync(Arg.Any<Func<Task<byte[]>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<byte[]>>>()());

        _mockAudioGenerator.GenerateAudioAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(audioData);

        // Act
        var result = await _parallelGenerator.GenerateAudioForArticlesAsync(articles, voiceId);

        // Assert
        result.Should().NotBeNull();
        result.SuccessfulGenerations.Should().ContainKey("article-1");
        result.SuccessfulGenerations["article-1"].Should().BeEquivalentTo(audioData);
        result.FailedGenerations.Should().BeEmpty();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);
        result.AllSuccessful.Should().BeTrue();
        result.AnySuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAudioForArticlesAsync_WithFailedGeneration_ReturnsFailureResult()
    {
        // Arrange
        var article = CreateTestArticle("article-1", "Test Article", "Test content");
        var articles = new List<Article> { article };
        var voiceId = "test-voice";

        _mockRateLimiter.ExecuteAsync(Arg.Any<Func<Task<byte[]>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<byte[]>>>()());

        _mockAudioGenerator.GenerateAudioAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<byte[]>(_ => throw new InvalidOperationException("API error"));

        // Act
        var result = await _parallelGenerator.GenerateAudioForArticlesAsync(articles, voiceId);

        // Assert
        result.Should().NotBeNull();
        result.SuccessfulGenerations.Should().BeEmpty();
        result.FailedGenerations.Should().ContainKey("article-1");
        result.FailedGenerations["article-1"].Should().Contain("InvalidOperationException");
        result.FailedGenerations["article-1"].Should().Contain("API error");
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(1);
        result.AllSuccessful.Should().BeFalse();
        result.AnySuccessful.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAudioForArticlesAsync_WithMixedResults_ReturnsPartialSuccess()
    {
        // Arrange
        var article1 = CreateTestArticle("article-1", "Success Article", "Content 1");
        var article2 = CreateTestArticle("article-2", "Failure Article", "Content 2");
        var articles = new List<Article> { article1, article2 };
        var voiceId = "test-voice";
        var audioData = new byte[] { 1, 2, 3, 4 };

        _mockRateLimiter.ExecuteAsync(Arg.Any<Func<Task<byte[]>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<byte[]>>>()());

        _mockAudioGenerator.GenerateAudioAsync("Content 1", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(audioData);

        _mockAudioGenerator.GenerateAudioAsync("Content 2", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<byte[]>(_ => throw new InvalidOperationException("Generation failed"));

        // Act
        var result = await _parallelGenerator.GenerateAudioForArticlesAsync(articles, voiceId);

        // Assert
        result.Should().NotBeNull();
        result.SuccessfulGenerations.Should().ContainKey("article-1");
        result.FailedGenerations.Should().ContainKey("article-2");
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(1);
        result.TotalProcessed.Should().Be(2);
        result.AllSuccessful.Should().BeFalse();
        result.AnySuccessful.Should().BeTrue();
    }

    [Fact]
    public void EstimateTotalCost_WithMultipleArticles_ReturnsSum()
    {
        // Arrange
        var article1 = CreateTestArticle("article-1", "Article 1", "Content 1");
        var article2 = CreateTestArticle("article-2", "Article 2", "Content 2");
        var articles = new List<Article> { article1, article2 };

        _mockAudioGenerator.EstimateCost("Content 1").Returns(0.50m);
        _mockAudioGenerator.EstimateCost("Content 2").Returns(0.75m);

        // Act
        var totalCost = _parallelGenerator.EstimateTotalCost(articles);

        // Assert
        totalCost.Should().Be(1.25m);
    }

    [Fact]
    public void EstimateTotalCharacters_WithMultipleArticles_ReturnsSum()
    {
        // Arrange
        var article1 = CreateTestArticle("article-1", "Article 1", "Content1"); // 8 chars
        var article2 = CreateTestArticle("article-2", "Article 2", "Content22"); // 9 chars
        var articles = new List<Article> { article1, article2 };

        // Act
        var totalCharacters = _parallelGenerator.EstimateTotalCharacters(articles);

        // Assert
        totalCharacters.Should().Be(17);
    }

    [Fact]
    public async Task GenerateAudioForArticlesAsync_UsesRateLimiter()
    {
        // Arrange
        var article = CreateTestArticle("article-1", "Test Article", "Test content");
        var articles = new List<Article> { article };
        var voiceId = "test-voice";
        var audioData = new byte[] { 1, 2, 3, 4 };

        _mockRateLimiter.ExecuteAsync(Arg.Any<Func<Task<byte[]>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<byte[]>>>()());

        _mockAudioGenerator.GenerateAudioAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(audioData);

        // Act
        await _parallelGenerator.GenerateAudioForArticlesAsync(articles, voiceId);

        // Assert
        await _mockRateLimiter.Received(1).ExecuteAsync(
            Arg.Any<Func<Task<byte[]>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAudioForArticlesAsync_PassesCorrectParametersToAudioGenerator()
    {
        // Arrange
        var article = CreateTestArticle("article-1", "Test Article", "Test content here");
        var articles = new List<Article> { article };
        var voiceId = "my-voice-id";
        var audioData = new byte[] { 1, 2, 3, 4 };

        _mockRateLimiter.ExecuteAsync(Arg.Any<Func<Task<byte[]>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<Task<byte[]>>>()());

        _mockAudioGenerator.GenerateAudioAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(audioData);

        // Act
        await _parallelGenerator.GenerateAudioForArticlesAsync(articles, voiceId);

        // Assert
        await _mockAudioGenerator.Received(1).GenerateAudioAsync(
            "Test content here",
            "my-voice-id",
            Arg.Any<CancellationToken>());
    }

    private static Article CreateTestArticle(string id, string title, string content)
    {
        return new Article
        {
            Id = id,
            Title = title,
            Content = content,
            Url = $"https://example.com/{id}",
            Author = "Test Author",
            ScrapedDate = DateTime.UtcNow
        };
    }
}
