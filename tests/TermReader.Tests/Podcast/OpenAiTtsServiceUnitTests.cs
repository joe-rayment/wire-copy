// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

/// <summary>
/// Unit tests for OpenAiTtsService error and configuration paths.
/// These complement the always-skipped integration tests that require a real API key.
/// </summary>
[Trait("Category", "Unit")]
public class OpenAiTtsServiceUnitTests
{
    [Fact]
    public void IsConfigured_NoApiKey_ReturnsFalse()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = null });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.IsConfigured.Should().BeFalse("no API key from any source");
    }

    [Fact]
    public void IsConfigured_EmptyApiKey_ReturnsFalse()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "  " });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.IsConfigured.Should().BeFalse("whitespace-only API key should not count");
    }

    [Fact]
    public void IsConfigured_WithApiKey_ReturnsTrue()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test-key-123" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WithSettingsStoreKey_ReturnsTrue()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = null });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiApiKey").Returns("sk-from-settings");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.IsConfigured.Should().BeTrue("API key should be found via settings store fallback");
    }

    [Fact]
    public async Task GenerateAudioAsync_NotConfigured_ReturnsFailure()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = null });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var result = await sut.GenerateAudioAsync("Some text", "Test Title");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    [Fact]
    public async Task GenerateAudioAsync_ExceedsBudget_ReturnsFailure()
    {
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = "sk-test-key",
            MaxBudgetUsd = 0.01m, // Very low budget
        });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        // Generate enough text to exceed the $0.01 budget at $15/million chars
        var longText = new string('a', 100_000); // ~$1.50 worth

        var result = await sut.GenerateAudioAsync(longText, "Expensive Article");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds budget");
    }

    [Fact]
    public async Task GenerateAudioAsync_NullText_ThrowsArgumentNull()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var act = () => sut.GenerateAudioAsync(null!, "Title");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GenerateAudioAsync_NullTitle_ThrowsArgumentNull()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var act = () => sut.GenerateAudioAsync("text", null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void EstimateCost_ShortText_CalculatesCorrectly()
    {
        var config = Options.Create(new OpenAiTtsConfiguration());
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var estimate = sut.EstimateCost("Hello world");

        estimate.CharacterCount.Should().Be(11);
        estimate.ChunkCount.Should().Be(1);
        estimate.EstimatedCostUsd.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EstimateCost_LongText_CalculatesMultipleChunks()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { MaxChunkSize = 4096 });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var longText = new string('a', 10_000);
        var estimate = sut.EstimateCost(longText);

        estimate.CharacterCount.Should().Be(10_000);
        estimate.ChunkCount.Should().Be(3, "10000 / 4096 rounds up to 3 chunks");
        estimate.EstimatedCostUsd.Should().Be(0.15m, "10000 * $15/1M = $0.15");
    }

    [Fact]
    public void EstimateCost_NullText_ThrowsArgumentNull()
    {
        var config = Options.Create(new OpenAiTtsConfiguration());
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var act = () => sut.EstimateCost(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
