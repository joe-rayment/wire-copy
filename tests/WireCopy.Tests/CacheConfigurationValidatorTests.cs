// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Configuration.Validation;
using Xunit;

namespace WireCopy.Tests;

[Trait("Category", "Unit")]
public class CacheConfigurationValidatorTests
{
    private readonly CacheConfigurationValidator _validator = new();

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        var config = new CacheConfiguration();

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ZeroMaxEntries_Fails()
    {
        var config = new CacheConfiguration { MaxEntries = 0 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxEntries");
    }

    [Fact]
    public void Validate_NegativeMaxSizeBytes_Fails()
    {
        var config = new CacheConfiguration { MaxSizeBytes = -1 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxSizeBytes");
    }

    [Fact]
    public void Validate_MaxEntrySizeExceedsMaxSize_Fails()
    {
        var config = new CacheConfiguration
        {
            MaxSizeBytes = 1000,
            MaxEntrySizeBytes = 2000
        };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("MaxEntrySizeBytes");
    }

    [Fact]
    public void Validate_ZeroDefaultTtl_Fails()
    {
        var config = new CacheConfiguration { DefaultTtlSeconds = 0 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("DefaultTtlSeconds");
    }

    [Fact]
    public void Validate_NegativeIdleThreshold_Fails()
    {
        var config = new CacheConfiguration { IdleThresholdMs = -1 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("IdleThresholdMs");
    }

    [Fact]
    public void Validate_ZeroIdleThreshold_Succeeds()
    {
        var config = new CacheConfiguration { IdleThresholdMs = 0 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ZeroPreloadDelay_Fails()
    {
        var config = new CacheConfiguration { PreloadDelayMs = 0 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("PreloadDelayMs");
    }

    [Fact]
    public void Validate_ZeroCircuitBreakerCooldown_Fails()
    {
        var config = new CacheConfiguration { CircuitBreakerCooldownSeconds = 0 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("CircuitBreakerCooldownSeconds");
    }
}
