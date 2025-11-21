// <copyright file="CommandOptionsTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using NYTAudioScraper.API;
using Xunit;

namespace NYTAudioScraper.Tests;

public class CommandOptionsTests
{
    [Fact]
    public void ScrapeOnly_DefaultValue_ShouldBeFalse()
    {
        // Arrange & Act
        var options = new CommandOptions();

        // Assert
        options.ScrapeOnly.Should().BeFalse();
    }

    [Fact]
    public void ScrapeOnly_SetToTrue_ShouldReturnTrue()
    {
        // Arrange & Act
        var options = new CommandOptions { ScrapeOnly = true };

        // Assert
        options.ScrapeOnly.Should().BeTrue();
    }

    [Fact]
    public void Validate_ScrapeOnlyAndTestMode_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ScrapeOnly = true,
            TestMode = true
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Cannot use --scrape-only with --test");
    }

    [Fact]
    public void Validate_ScrapeOnlyOnly_ShouldNotReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ScrapeOnly = true,
            TestMode = false
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().NotContain(e => e.Contains("scrape-only"));
    }

    [Fact]
    public void Validate_TestModeOnly_ShouldNotReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ScrapeOnly = false,
            TestMode = true
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().NotContain(e => e.Contains("scrape-only"));
    }

    [Fact]
    public void Validate_ValidUrl_ShouldNotReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ArticleUrl = "https://www.nytimes.com/2024/01/01/technology/sample-article.html"
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidUrl_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ArticleUrl = "not-a-valid-url"
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Invalid article URL");
    }

    [Fact]
    public void Validate_NonNYTUrl_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ArticleUrl = "https://www.example.com/article"
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("nytimes.com domain");
    }

    [Fact]
    public void Validate_NegativeArticleCount_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ArticleCount = -1
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Article count must be positive");
    }

    [Fact]
    public void Validate_ArticleCountOver1000_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ArticleCount = 1001
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Article count cannot exceed 1000");
    }

    [Fact]
    public void Validate_NegativeBudget_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            Budget = -1.0m
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Budget cannot be negative");
    }

    [Fact]
    public void Validate_BudgetOver1000_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            Budget = 1001.0m
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Budget cannot exceed $1000");
    }

    [Fact]
    public void Validate_InvalidVoiceId_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            VoiceId = "invalid voice id with spaces!"
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Invalid voice ID format");
    }

    [Fact]
    public void Validate_ValidVoiceId_ShouldNotReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            VoiceId = "valid-voice-id_123"
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_NonExistentCookieFile_ShouldReturnError()
    {
        // Arrange
        var options = new CommandOptions
        {
            ImportCookiesPath = "/nonexistent/path/cookies.json"
        };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle()
            .Which.Should().Contain("Cookie file not found");
    }
}
