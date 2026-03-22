// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Configuration.Validation;
using Xunit;

namespace TermReader.Tests.Podcast;

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
    public void Validate_ValidBucketName_Succeeds()
    {
        var config = new GcsConfiguration { BucketName = "my-valid-bucket-123" };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("A")]
    public void Validate_TooShortBucketName_Fails(string name)
    {
        var config = new GcsConfiguration { BucketName = name };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketName");
    }

    [Fact]
    public void Validate_TooLongBucketName_Fails()
    {
        var config = new GcsConfiguration { BucketName = new string('a', 64) };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketName");
    }

    [Theory]
    [InlineData("My-Bucket")]
    [InlineData("-invalid-start")]
    [InlineData("invalid-end-")]
    public void Validate_InvalidBucketNameFormat_Fails(string name)
    {
        var config = new GcsConfiguration { BucketName = name };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketName");
    }

    [Fact]
    public void Validate_NullBucketName_Succeeds()
    {
        var config = new GcsConfiguration { BucketName = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_EmptyBucketLocation_Fails(string location)
    {
        var config = new GcsConfiguration { BucketLocation = location };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BucketLocation");
    }

    [Fact]
    public void Validate_NonExistentServiceAccountKeyPath_Fails()
    {
        var config = new GcsConfiguration
        {
            ServiceAccountKeyPath = "/nonexistent/path/key.json",
        };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("ServiceAccountKeyPath");
    }

    [Fact]
    public void Validate_NullServiceAccountKeyPath_Succeeds()
    {
        var config = new GcsConfiguration { ServiceAccountKeyPath = null };

        var result = _validator.Validate(null, config);

        result.Succeeded.Should().BeTrue();
    }
}
