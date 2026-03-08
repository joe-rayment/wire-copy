// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Configuration.Validation;
using Xunit;

namespace TermReader.Tests;

[Trait("Category", "Unit")]
public class GcsConfigurationValidatorTests
{
    private readonly GcsConfigurationValidator _validator = new();

    [Fact]
    public void Validate_DefaultConfig_Succeeds()
    {
        var config = new GcsConfiguration();

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NullBucketName_Succeeds()
    {
        var config = new GcsConfiguration { BucketName = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("my-podcast-bucket")]
    [InlineData("termreader.podcasts")]
    [InlineData("abc")]
    public void Validate_ValidBucketName_Succeeds(string bucketName)
    {
        var config = new GcsConfiguration { BucketName = bucketName };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a")]
    public void Validate_BucketNameTooShort_Fails(string bucketName)
    {
        var config = new GcsConfiguration { BucketName = bucketName };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketName");
    }

    [Fact]
    public void Validate_BucketNameTooLong_Fails()
    {
        var config = new GcsConfiguration { BucketName = new string('a', 64) };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketName");
    }

    [Theory]
    [InlineData("My-Bucket")]
    [InlineData("UPPERCASE")]
    [InlineData("has spaces")]
    public void Validate_BucketNameInvalidChars_Fails(string bucketName)
    {
        var config = new GcsConfiguration { BucketName = bucketName };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketName");
    }

    [Fact]
    public void Validate_NonExistentServiceAccountKey_Fails()
    {
        var config = new GcsConfiguration { ServiceAccountKeyPath = "/nonexistent/path/key.json" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("ServiceAccountKeyPath");
    }

    [Fact]
    public void Validate_NullServiceAccountKey_Succeeds()
    {
        var config = new GcsConfiguration { ServiceAccountKeyPath = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyBucketLocation_Fails()
    {
        var config = new GcsConfiguration { BucketLocation = "" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketLocation");
    }
}
