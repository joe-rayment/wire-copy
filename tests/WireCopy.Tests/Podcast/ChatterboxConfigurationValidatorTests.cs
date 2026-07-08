// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Configuration.Validation;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class ChatterboxConfigurationValidatorTests
{
    private readonly ChatterboxConfigurationValidator _validator = new();

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        var config = new ChatterboxConfiguration();

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyUvPath_Fails(string uvPath)
    {
        var config = new ChatterboxConfiguration { UvPath = uvPath };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("UvPath");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyWorkerRelativePath_Fails(string workerPath)
    {
        var config = new ChatterboxConfiguration { WorkerRelativePath = workerPath };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("WorkerRelativePath");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("cuda")]
    [InlineData("mps")]
    [InlineData("cpu")]
    [InlineData("CPU")]
    [InlineData("Auto")]
    public void Validate_ValidDevice_Succeeds(string device)
    {
        var config = new ChatterboxConfiguration { Device = device };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("")]
    [InlineData("metal")]
    public void Validate_InvalidDevice_Fails(string device)
    {
        var config = new ChatterboxConfiguration { Device = device };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Device");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Validate_CfgWeightBoundaries_Succeed(float cfgWeight)
    {
        var config = new ChatterboxConfiguration { CfgWeight = cfgWeight };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    public void Validate_CfgWeightOutOfRange_Fails(float cfgWeight)
    {
        var config = new ChatterboxConfiguration { CfgWeight = cfgWeight };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("CfgWeight");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(2f)]
    public void Validate_DefaultExaggerationBoundaries_Succeed(float exaggeration)
    {
        var config = new ChatterboxConfiguration { DefaultExaggeration = exaggeration };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(2.01f)]
    public void Validate_DefaultExaggerationOutOfRange_Fails(float exaggeration)
    {
        var config = new ChatterboxConfiguration { DefaultExaggeration = exaggeration };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("DefaultExaggeration");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(300)]
    [InlineData(1000)]
    public void Validate_MaxChunkSizeInRange_Succeeds(int maxChunkSize)
    {
        var config = new ChatterboxConfiguration { MaxChunkSize = maxChunkSize };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(49)]
    [InlineData(1001)]
    [InlineData(0)]
    public void Validate_MaxChunkSizeOutOfRange_Fails(int maxChunkSize)
    {
        var config = new ChatterboxConfiguration { MaxChunkSize = maxChunkSize };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxChunkSize");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveStartTimeout_Fails(int seconds)
    {
        var config = new ChatterboxConfiguration { StartTimeoutSeconds = seconds };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("StartTimeoutSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveLoadTimeout_Fails(int seconds)
    {
        var config = new ChatterboxConfiguration { LoadTimeoutSeconds = seconds };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("LoadTimeoutSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveSpeakTimeout_Fails(int seconds)
    {
        var config = new ChatterboxConfiguration { SpeakTimeoutSecondsPerChunk = seconds };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("SpeakTimeoutSecondsPerChunk");
    }

    [Fact]
    public void Validate_MultipleInvalidFields_ReportsAll()
    {
        var config = new ChatterboxConfiguration
        {
            UvPath = string.Empty,
            Device = "gpu",
            MaxChunkSize = 0,
        };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("UvPath");
        result.FailureMessage.Should().Contain("Device");
        result.FailureMessage.Should().Contain("MaxChunkSize");
    }
}
