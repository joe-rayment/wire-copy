// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Infrastructure.Configuration.Validation;
using TermReader.Infrastructure.Podcast.Cache;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Unit")]
public class TtsAudioCacheConfigurationValidatorTests
{
    private readonly TtsAudioCacheConfigurationValidator _validator = new();

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        var config = new TtsAudioCacheConfiguration();

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_InvalidMaxSizeBytes_Fails(long maxSizeBytes)
    {
        var config = new TtsAudioCacheConfiguration { MaxSizeBytes = maxSizeBytes };

        var result = _validator.Validate(null, config);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(TtsAudioCacheConfiguration.MaxSizeBytes));
    }

    [Fact]
    public void Validate_ZeroTtl_Fails()
    {
        var config = new TtsAudioCacheConfiguration { Ttl = TimeSpan.Zero };

        var result = _validator.Validate(null, config);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(TtsAudioCacheConfiguration.Ttl));
    }

    [Fact]
    public void Validate_NegativeTtl_Fails()
    {
        var config = new TtsAudioCacheConfiguration { Ttl = TimeSpan.FromHours(-1) };

        var result = _validator.Validate(null, config);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(TtsAudioCacheConfiguration.Ttl));
    }

    [Fact]
    public void Validate_ValidBasePath_Succeeds()
    {
        var config = new TtsAudioCacheConfiguration { BasePath = "/tmp/tts-cache" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NullBasePath_Succeeds()
    {
        var config = new TtsAudioCacheConfiguration { BasePath = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_CustomMaxSizeBytes_Succeeds()
    {
        var config = new TtsAudioCacheConfiguration { MaxSizeBytes = 1024 * 1024 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_CustomTtl_Succeeds()
    {
        var config = new TtsAudioCacheConfiguration { Ttl = TimeSpan.FromDays(7) };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }
}
