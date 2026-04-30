// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Configuration.Validation;
using Xunit;

namespace WireCopy.Tests.Podcast;

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

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyAudioCodec_Fails(string codec)
    {
        var config = new PodcastConfiguration { AudioCodec = codec };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("AudioCodec");
    }

    [Theory]
    [InlineData("64")]
    [InlineData("k")]
    [InlineData("abc")]
    [InlineData("64kbps")]
    public void Validate_InvalidAudioBitrate_Fails(string bitrate)
    {
        var config = new PodcastConfiguration { AudioBitrate = bitrate };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("AudioBitrate");
    }

    [Theory]
    [InlineData("64k")]
    [InlineData("128k")]
    [InlineData("256k")]
    public void Validate_ValidAudioBitrate_Succeeds(string bitrate)
    {
        var config = new PodcastConfiguration { AudioBitrate = bitrate };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Validate_InvalidAudioChannels_Fails(int channels)
    {
        var config = new PodcastConfiguration { AudioChannels = channels };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("AudioChannels");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Validate_ValidAudioChannels_Succeeds(int channels)
    {
        var config = new PodcastConfiguration { AudioChannels = channels };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11025)]
    [InlineData(96000)]
    public void Validate_InvalidSampleRate_Fails(int sampleRate)
    {
        var config = new PodcastConfiguration { SampleRate = sampleRate };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("SampleRate");
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Validate_ValidSampleRate_Succeeds(int sampleRate)
    {
        var config = new PodcastConfiguration { SampleRate = sampleRate };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NonExistentTempDirectory_Fails()
    {
        var config = new PodcastConfiguration { TempDirectory = "/nonexistent/temp/dir" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("TempDirectory");
    }

    [Fact]
    public void Validate_NullTempDirectory_Succeeds()
    {
        var config = new PodcastConfiguration { TempDirectory = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyTitle_Fails(string title)
    {
        var config = new PodcastConfiguration { Title = title };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Title");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyAuthor_Fails(string author)
    {
        var config = new PodcastConfiguration { Author = author };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Author");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyLanguage_Fails(string language)
    {
        var config = new PodcastConfiguration { Language = language };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("Language");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/image.jpg")]
    public void Validate_InvalidImageUrl_Fails(string imageUrl)
    {
        var config = new PodcastConfiguration { ImageUrl = imageUrl };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("ImageUrl");
    }

    [Fact]
    public void Validate_ValidHttpsImageUrl_Succeeds()
    {
        var config = new PodcastConfiguration { ImageUrl = "https://example.com/image.jpg" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NullImageUrl_Succeeds()
    {
        var config = new PodcastConfiguration { ImageUrl = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }
}
