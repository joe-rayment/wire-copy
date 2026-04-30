// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Configuration.Validation;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class OpenAiTtsConfigurationValidatorTests
{
    private readonly OpenAiTtsConfigurationValidator _validator = new();

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        var config = new OpenAiTtsConfiguration();

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyModel_Fails(string model)
    {
        var config = new OpenAiTtsConfiguration { Model = model };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Model");
    }

    [Theory]
    [InlineData("invalid-voice")]
    [InlineData("siri")]
    public void Validate_InvalidVoice_Fails(string voice)
    {
        var config = new OpenAiTtsConfiguration { Voice = voice };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Voice");
    }

    [Theory]
    [InlineData("alloy")]
    [InlineData("nova")]
    [InlineData("shimmer")]
    public void Validate_ValidVoice_Succeeds(string voice)
    {
        var config = new OpenAiTtsConfiguration { Voice = voice };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.24f)]
    [InlineData(4.1f)]
    [InlineData(-1.0f)]
    public void Validate_InvalidSpeed_Fails(float speed)
    {
        var config = new OpenAiTtsConfiguration { Speed = speed };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Speed");
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(1.0f)]
    [InlineData(4.0f)]
    public void Validate_ValidSpeed_Succeeds(float speed)
    {
        var config = new OpenAiTtsConfiguration { Speed = speed };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("m4a")]
    public void Validate_InvalidOutputFormat_Fails(string format)
    {
        var config = new OpenAiTtsConfiguration { OutputFormat = format };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("OutputFormat");
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("aac")]
    [InlineData("flac")]
    public void Validate_ValidOutputFormat_Succeeds(string format)
    {
        var config = new OpenAiTtsConfiguration { OutputFormat = format };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Validate_InvalidMaxChunkSize_Fails(int chunkSize)
    {
        var config = new OpenAiTtsConfiguration { MaxChunkSize = chunkSize };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxChunkSize");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidMaxBudgetUsd_Fails(int budget)
    {
        var config = new OpenAiTtsConfiguration { MaxBudgetUsd = budget };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxBudgetUsd");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validate_InvalidMaxRetries_Fails(int retries)
    {
        var config = new OpenAiTtsConfiguration { MaxRetries = retries };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxRetries");
    }

    [Fact]
    public void Validate_NegativeRetryBaseDelayMs_Fails()
    {
        var config = new OpenAiTtsConfiguration { RetryBaseDelayMs = -1 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("RetryBaseDelayMs");
    }

    [Fact]
    public void Validate_NegativeInterChunkDelayMs_Fails()
    {
        var config = new OpenAiTtsConfiguration { InterChunkDelayMs = -1 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("InterChunkDelayMs");
    }
}
