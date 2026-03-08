// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Integration")]
public class OpenAiTtsIntegrationTests
{
    private static string? ApiKey => Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    private static bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    private OpenAiTtsService CreateService()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = ApiKey! });
        return new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);
    }

    [Fact(Skip = "Requires OPENAI_API_KEY environment variable")]
    public void IsConfigured_WithApiKey_ReturnsTrue()
    {
        if (!HasApiKey)
        {
            return;
        }

        var service = CreateService();
        service.IsConfigured.Should().BeTrue();
    }

    [Fact(Skip = "Requires OPENAI_API_KEY environment variable")]
    public async Task GenerateAudioAsync_ShortText_ReturnsNonEmptyAudio()
    {
        if (!HasApiKey)
        {
            return;
        }

        var service = CreateService();

        var result = await service.GenerateAudioAsync("Hello world. This is a test.", "Test");

        result.Success.Should().BeTrue();
        result.AudioData.Should().NotBeNullOrEmpty();
        result.CharactersProcessed.Should().BeGreaterThan(0);
    }

    [Fact(Skip = "Requires OPENAI_API_KEY environment variable")]
    public async Task GenerateAudioAsync_LongText_ChunksAndReturnsAudio()
    {
        if (!HasApiKey)
        {
            return;
        }

        var service = CreateService();

        // Generate text exceeding the default 4096 char chunk size
        var longText = string.Join(" ", Enumerable.Repeat(
            "This is a sentence that will be repeated many times to create a long text for chunking.", 60));
        longText.Length.Should().BeGreaterThan(4096);

        var result = await service.GenerateAudioAsync(longText, "Long Text Test");

        result.Success.Should().BeTrue();
        result.AudioData.Should().NotBeNullOrEmpty();
        result.ChunksCompleted.Should().BeGreaterThan(1);
    }

    [Fact(Skip = "Requires OPENAI_API_KEY environment variable")]
    public async Task GenerateAudioAsync_ReturnsValidAacAudio()
    {
        if (!HasApiKey)
        {
            return;
        }

        var service = CreateService();

        var result = await service.GenerateAudioAsync("Hello world.", "AAC Test");

        result.Success.Should().BeTrue();
        result.AudioData.Should().NotBeNull();
        result.AudioData!.Length.Should().BeGreaterThan(100);

        // Verify AAC/M4A container: should start with an ADTS sync word (0xFF 0xFx)
        // or an MPEG-4 container (ftyp atom). The OpenAI API returns raw AAC by default.
        var firstByte = result.AudioData[0];
        var secondByte = result.AudioData[1];
        var isAdts = firstByte == 0xFF && (secondByte & 0xF0) == 0xF0;
        var isMp4 = result.AudioData.Length > 7 &&
            System.Text.Encoding.ASCII.GetString(result.AudioData, 4, 4) == "ftyp";
        (isAdts || isMp4).Should().BeTrue("audio should be ADTS AAC or MPEG-4 container");
    }
}
