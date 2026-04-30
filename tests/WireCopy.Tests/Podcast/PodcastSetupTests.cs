// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class PodcastSetupTests
{
    #region TTS API Key Override

    [Fact]
    public void SetApiKeyOverride_SetsKeyAndBecomesConfigured()
    {
        var service = CreateTtsService(apiKey: null);

        service.IsConfigured.Should().BeFalse("no key set yet");

        service.SetApiKeyOverride("sk-test-key");

        service.IsConfigured.Should().BeTrue("override key was set");
    }

    [Fact]
    public void SetApiKeyOverride_OverridesConfigKey()
    {
        var service = CreateTtsService(apiKey: "config-key");
        service.IsConfigured.Should().BeTrue();

        service.SetApiKeyOverride("override-key");

        // Override takes priority — service should still be configured
        service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SetApiKeyOverride_WhitespaceOnly_RemainsNotConfigured()
    {
        var service = CreateTtsService(apiKey: null);

        service.SetApiKeyOverride("   ");

        service.IsConfigured.Should().BeFalse("whitespace-only key is treated as unconfigured");
    }

    [Fact]
    public void SetApiKeyOverride_EmptyString_RemainsNotConfigured()
    {
        var service = CreateTtsService(apiKey: null);

        service.SetApiKeyOverride(string.Empty);

        service.IsConfigured.Should().BeFalse("empty string key is treated as unconfigured");
    }

    [Fact]
    public void IsConfigured_NoKeyAnywhere_ReturnsFalse()
    {
        var service = CreateTtsService(apiKey: null);

        service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ConfigKeySet_ReturnsTrue()
    {
        var service = CreateTtsService(apiKey: "sk-from-config");

        service.IsConfigured.Should().BeTrue();
    }

    #endregion

    #region GCS Bucket Validation

    [Theory]
    [InlineData("my-bucket", true)]
    [InlineData("my.bucket.name", true)]
    [InlineData("bucket123", true)]
    [InlineData("a-b", true)]
    [InlineData("abc", true)]
    public void IsValidBucketName_ValidNames_ReturnsTrue(string name, bool expected)
    {
        GcsConfiguration.IsValidBucketName(name).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]           // Too short
    [InlineData("-bucket")]      // Starts with hyphen
    [InlineData("bucket-")]      // Ends with hyphen
    [InlineData("MY-BUCKET")]    // Uppercase not allowed
    [InlineData("a")]            // Too short (1 char)
    public void IsValidBucketName_InvalidNames_ReturnsFalse(string? name)
    {
        GcsConfiguration.IsValidBucketName(name).Should().BeFalse();
    }

    [Fact]
    public void IsValidBucketName_MaxLength63_ReturnsTrue()
    {
        var name = "a" + new string('b', 61) + "c"; // 63 chars
        GcsConfiguration.IsValidBucketName(name).Should().BeTrue();
    }

    [Fact]
    public void IsValidBucketName_Exceeds63Chars_ReturnsFalse()
    {
        var name = "a" + new string('b', 62) + "c"; // 64 chars
        GcsConfiguration.IsValidBucketName(name).Should().BeFalse();
    }

    [Fact]
    public void GcsConfiguration_BucketName_IsSettable()
    {
        var config = new GcsConfiguration();
        config.BucketName.Should().BeNull("default is null");

        config.BucketName = "my-podcast-bucket";
        config.BucketName.Should().Be("my-podcast-bucket");
    }

    #endregion

    #region Helpers

    private static OpenAiTtsService CreateTtsService(string? apiKey)
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = apiKey });
        return new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);
    }

    #endregion
}
