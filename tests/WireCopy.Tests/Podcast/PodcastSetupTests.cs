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

}
