// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Configuration.Validation;
using Xunit;

namespace TermReader.Tests;

[Trait("Category", "Unit")]
public class PodcastConfigurationValidatorTests
{
    private readonly PodcastConfigurationValidator _validator = new();

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        var config = new PodcastConfiguration();

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyAudioCodec_Fails()
    {
        var config = new PodcastConfiguration { AudioCodec = "" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("AudioCodec");
    }

    [Theory]
    [InlineData("64k")]
    [InlineData("128k")]
    [InlineData("256K")]
    public void Validate_ValidBitrate_Succeeds(string bitrate)
    {
        var config = new PodcastConfiguration { AudioBitrate = bitrate };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("64")]
    [InlineData("k64")]
    [InlineData("")]
    public void Validate_InvalidBitrate_Fails(string bitrate)
    {
        var config = new PodcastConfiguration { AudioBitrate = bitrate };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("AudioBitrate");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Validate_ValidChannels_Succeeds(int channels)
    {
        var config = new PodcastConfiguration { AudioChannels = channels };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Validate_InvalidChannels_Fails(int channels)
    {
        var config = new PodcastConfiguration { AudioChannels = channels };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("AudioChannels");
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(22050)]
    public void Validate_ValidSampleRate_Succeeds(int sampleRate)
    {
        var config = new PodcastConfiguration { SampleRate = sampleRate };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_InvalidSampleRate_Fails()
    {
        var config = new PodcastConfiguration { SampleRate = 12345 };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("SampleRate");
    }

    [Fact]
    public void Validate_EmptyTitle_Fails()
    {
        var config = new PodcastConfiguration { Title = "" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Title");
    }

    [Fact]
    public void Validate_EmptyAuthor_Fails()
    {
        var config = new PodcastConfiguration { Author = "" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Author");
    }

    [Fact]
    public void Validate_EmptyLanguage_Fails()
    {
        var config = new PodcastConfiguration { Language = "" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Language");
    }

    [Fact]
    public void Validate_ValidImageUrl_Succeeds()
    {
        var config = new PodcastConfiguration { ImageUrl = "https://example.com/cover.jpg" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_InvalidImageUrl_Fails()
    {
        var config = new PodcastConfiguration { ImageUrl = "not-a-url" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("ImageUrl");
    }

    [Fact]
    public void Validate_NonHttpImageUrl_Fails()
    {
        var config = new PodcastConfiguration { ImageUrl = "ftp://example.com/cover.jpg" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("ImageUrl");
    }

    [Fact]
    public void Validate_NullImageUrl_Succeeds()
    {
        var config = new PodcastConfiguration { ImageUrl = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }
}
