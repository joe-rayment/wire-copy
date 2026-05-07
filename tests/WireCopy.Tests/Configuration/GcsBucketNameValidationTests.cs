// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Configuration;

/// <summary>
/// Tests for the bucket name validator wired into the Setup screen
/// (workspace-dwgl). Covers the lexical regex and the additional
/// FormField-level guards on top.
/// </summary>
[Trait("Category", "Unit")]
public class GcsBucketNameValidationTests
{
    [Fact]
    public void IsValidBucketName_RejectsUnderscore()
    {
        // workspace-dwgl: tightened regex no longer allows `_`.
        GcsConfiguration.IsValidBucketName("my_bucket-name").Should().BeFalse();
    }

    [Theory]
    [InlineData("my-valid-bucket-123")]
    [InlineData("a-b-c")]
    [InlineData("abc.def.ghi")]
    public void IsValidBucketName_AcceptsLowercaseDigitsHyphensDots(string name)
    {
        GcsConfiguration.IsValidBucketName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("My-Bucket")]
    [InlineData("ab")]
    [InlineData("invalid_name")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    public void IsValidBucketName_RejectsBadInput(string name)
    {
        GcsConfiguration.IsValidBucketName(name).Should().BeFalse();
    }

    // --- Setup-screen FormField validator tests ---

    [Fact]
    public void Validator_Empty_ReturnsExactMessage()
    {
        SettingsCommandHandler.ValidateBucketNameInput("")
            .Should().Be("Bucket name cannot be empty");
        SettingsCommandHandler.ValidateBucketNameInput("   ")
            .Should().Be("Bucket name cannot be empty");
    }

    [Fact]
    public void Validator_Short_ReturnsExactMessage()
    {
        SettingsCommandHandler.ValidateBucketNameInput("ab")
            .Should().Be("Too short — bucket names must be 3-63 chars");
    }

    [Fact]
    public void Validator_TooShortAfterStrip_ReturnsExactMessage()
    {
        SettingsCommandHandler.ValidateBucketNameInput("gs://ab")
            .Should().Be("Too short — bucket names must be 3-63 chars");
    }

    [Fact]
    public void Validator_TooLong_ReturnsExactMessage()
    {
        var name = new string('a', 64);
        SettingsCommandHandler.ValidateBucketNameInput(name)
            .Should().Be("Too long — bucket names must be 3-63 chars");
    }

    [Theory]
    [InlineData("My-Bucket")]            // capital letters
    [InlineData("my_bucket-name")]       // underscore (newly disallowed)
    [InlineData("foo..bar")]             // double dot
    [InlineData("googfoo")]              // reserved goog prefix
    public void Validator_BadChars_ReturnsExactMessage(string input)
    {
        SettingsCommandHandler.ValidateBucketNameInput(input)
            .Should().Be("Use only lowercase a-z, 0-9, hyphens, dots (no underscores, no caps)");
    }

    [Theory]
    [InlineData("my-bucket")]
    [InlineData("gs://my-bucket")]
    public void Validator_AcceptsValid(string input)
    {
        SettingsCommandHandler.ValidateBucketNameInput(input).Should().BeNull();
    }

    [Fact]
    public void NormalizeBucketName_StripsGsPrefix()
    {
        SettingsCommandHandler.NormalizeBucketName("gs://my-bucket").Should().Be("my-bucket");
    }

    [Fact]
    public void NormalizeBucketName_TrimsWhitespace()
    {
        SettingsCommandHandler.NormalizeBucketName("  my-bucket  ").Should().Be("my-bucket");
    }

    [Fact]
    public void NormalizeBucketName_LeavesBareNameUnchanged()
    {
        SettingsCommandHandler.NormalizeBucketName("my-bucket").Should().Be("my-bucket");
    }
}
