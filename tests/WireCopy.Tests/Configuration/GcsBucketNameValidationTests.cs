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
    public void IsValidBucketName_AcceptsUnderscoreWithoutDots()
    {
        // workspace-wooa: underscores ARE valid per GCS naming rules as long
        // as the name does not also contain dots (the DNS-style restriction).
        GcsConfiguration.IsValidBucketName("my_bucket_name").Should().BeTrue();
        GcsConfiguration.IsValidBucketName("tr_list_reader").Should().BeTrue();
    }

    [Fact]
    public void IsValidBucketName_RejectsMixedUnderscoreAndDot()
    {
        // workspace-wooa: DNS rule — bucket names with dots cannot contain
        // underscores. Mixed underscore+dot names are rejected.
        GcsConfiguration.IsValidBucketName("my_bucket.example").Should().BeFalse();
    }

    [Theory]
    [InlineData("my-valid-bucket-123")]
    [InlineData("a-b-c")]
    [InlineData("abc.def.ghi")]
    [InlineData("my_bucket_name")]
    [InlineData("tr_list_reader")]
    public void IsValidBucketName_AcceptsLowercaseDigitsHyphensUnderscoresDots(string name)
    {
        GcsConfiguration.IsValidBucketName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("My-Bucket")]
    [InlineData("ab")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("name.with_underscore")]
    [InlineData("foo..bar")]
    public void IsValidBucketName_RejectsBadInput(string name)
    {
        GcsConfiguration.IsValidBucketName(name).Should().BeFalse();
    }

    // --- ExplainBucketInvalid: per-rule message tests (workspace-wooa) ---

    [Fact]
    public void Explain_Empty_ReturnsEmptyMessage()
    {
        GcsConfiguration.ExplainBucketInvalid("")
            .Should().Be("Bucket name cannot be empty");
        GcsConfiguration.ExplainBucketInvalid("   ")
            .Should().Be("Bucket name cannot be empty");
    }

    [Fact]
    public void Explain_Short_ReturnsLengthMessage()
    {
        GcsConfiguration.ExplainBucketInvalid("ab")
            .Should().Be("Too short — bucket names must be at least 3 characters");
    }

    [Fact]
    public void Explain_Long_ReturnsLengthMessage()
    {
        GcsConfiguration.ExplainBucketInvalid(new string('a', 64))
            .Should().Be("Too long — bucket names must be at most 63 characters");
    }

    [Fact]
    public void Explain_Uppercase_ReturnsCaseMessage()
    {
        GcsConfiguration.ExplainBucketInvalid("My-Bucket")
            .Should().Be("Use only lowercase letters — uppercase is not allowed");
    }

    [Fact]
    public void Explain_DoubleDot_ReturnsDoubleDotMessage()
    {
        GcsConfiguration.ExplainBucketInvalid("foo..bar")
            .Should().Be("Bucket names cannot contain two consecutive dots");
    }

    [Fact]
    public void Explain_GoogPrefix_ReturnsReservedMessage()
    {
        GcsConfiguration.ExplainBucketInvalid("googfoo")
            .Should().Be("Bucket names cannot begin with the reserved \"goog\" prefix");
    }

    [Fact]
    public void Explain_MixedUnderscoreAndDot_ReturnsDnsMessage()
    {
        GcsConfiguration.ExplainBucketInvalid("my_bucket.example")
            .Should().Be("Bucket names with dots cannot also contain underscores");
    }

    [Fact]
    public void Explain_LeadingHyphen_ReturnsBoundaryMessage()
    {
        GcsConfiguration.ExplainBucketInvalid("-leading-hyphen")
            .Should().Be("Must start and end with a letter or number");
    }

    [Theory]
    [InlineData("my-bucket")]
    [InlineData("my_bucket")]
    [InlineData("tr_list_reader")]
    [InlineData("abc.def.ghi")]
    public void Explain_Valid_ReturnsNull(string name)
    {
        GcsConfiguration.ExplainBucketInvalid(name).Should().BeNull();
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
            .Should().Be("Too short — bucket names must be at least 3 characters");
    }

    [Fact]
    public void Validator_TooShortAfterStrip_ReturnsExactMessage()
    {
        SettingsCommandHandler.ValidateBucketNameInput("gs://ab")
            .Should().Be("Too short — bucket names must be at least 3 characters");
    }

    [Fact]
    public void Validator_TooLong_ReturnsExactMessage()
    {
        var name = new string('a', 64);
        SettingsCommandHandler.ValidateBucketNameInput(name)
            .Should().Be("Too long — bucket names must be at most 63 characters");
    }

    [Theory]
    [InlineData("My-Bucket", "Use only lowercase letters — uppercase is not allowed")]
    [InlineData("foo..bar", "Bucket names cannot contain two consecutive dots")]
    [InlineData("googfoo", "Bucket names cannot begin with the reserved \"goog\" prefix")]
    [InlineData("my_name.example", "Bucket names with dots cannot also contain underscores")]
    public void Validator_BadChars_ReturnsRuleSpecificMessage(string input, string expected)
    {
        SettingsCommandHandler.ValidateBucketNameInput(input)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("my-bucket")]
    [InlineData("gs://my-bucket")]
    [InlineData("my_bucket")]
    [InlineData("tr_list_reader")]
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
