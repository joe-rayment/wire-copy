// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Configuration.Validation;
using Xunit;

namespace TermReader.Tests;

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

    [Fact]
    public void Validate_NullApiKey_Succeeds()
    {
        var config = new OpenAiTtsConfiguration { ApiKey = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyModel_Fails()
    {
        var config = new OpenAiTtsConfiguration { Model = "" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Model");
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

    [Fact]
    public void Validate_InvalidVoice_Fails()
    {
        var config = new OpenAiTtsConfiguration { Voice = "invalid" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Voice");
    }

    [Theory]
    [InlineData(0.24f)]
    [InlineData(4.1f)]
    public void Validate_SpeedOutOfRange_Fails(float speed)
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
    public void Validate_SpeedInRange_Succeeds(float speed)
    {
        var config = new OpenAiTtsConfiguration { Speed = speed };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_InvalidOutputFormat_Fails()
    {
        var config = new OpenAiTtsConfiguration { OutputFormat = "invalid" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("OutputFormat");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void Validate_MaxChunkSizeOutOfRange_Fails(int chunkSize)
    {
        var config = new OpenAiTtsConfiguration { MaxChunkSize = chunkSize };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxChunkSize");
    }

    [Fact]
    public void Validate_ZeroBudget_Fails()
    {
        var config = new OpenAiTtsConfiguration { MaxBudgetUsd = 0m };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxBudgetUsd");
    }

    [Fact]
    public void Validate_NegativeRetryDelay_Fails()
    {
        var config = new OpenAiTtsConfiguration { RetryBaseDelayMs = -1 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("RetryBaseDelayMs");
    }

    [Fact]
    public void Validate_NegativeInterChunkDelay_Fails()
    {
        var config = new OpenAiTtsConfiguration { InterChunkDelayMs = -1 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("InterChunkDelayMs");
    }

    [Fact]
    public void Validate_MaxRetriesOutOfRange_Fails()
    {
        var config = new OpenAiTtsConfiguration { MaxRetries = 11 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxRetries");
    }
}
