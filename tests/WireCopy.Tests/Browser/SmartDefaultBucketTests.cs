// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the smart-default bucket-name helper (workspace-76ig). When the
/// user opens the GCS bucket Setup row and has already saved a service-account
/// JSON, the bucket field pre-fills with "{project_id}-wirecopy-feed" so the
/// user can accept with Enter instead of typing a name from scratch.
/// </summary>
[Trait("Category", "Unit")]
public class SmartDefaultBucketTests
{
    [Fact]
    public void BuildSmartDefaultBucket_AppendsWirecopyFeedSuffix()
    {
        SettingsCommandHandler.BuildSmartDefaultBucket("my-project")
            .Should().Be("my-project-wirecopy-feed");
    }

    [Fact]
    public void BuildSmartDefaultBucket_TrimsWhitespace()
    {
        SettingsCommandHandler.BuildSmartDefaultBucket("  my-project  ")
            .Should().Be("my-project-wirecopy-feed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildSmartDefaultBucket_ReturnsNullForMissingProject(string? projectId)
    {
        SettingsCommandHandler.BuildSmartDefaultBucket(projectId).Should().BeNull();
    }

    [Fact]
    public void BuildSmartDefaultBucket_ReturnsNullWhenResultViolatesValidation()
    {
        // Project id with uppercase makes the derived bucket invalid.
        SettingsCommandHandler.BuildSmartDefaultBucket("My-Project").Should().BeNull();
    }

    [Fact]
    public void BuildSmartDefaultBucket_ReturnsNullWhenResultExceedsMaxLength()
    {
        // "<48-char project>-wirecopy-feed" = 48 + 14 = 62 — still valid
        // "<49-char project>-wirecopy-feed" = 49 + 14 = 63 — still valid
        // "<50-char project>-wirecopy-feed" = 50 + 14 = 64 — too long
        var fiftyChar = new string('a', 50);
        SettingsCommandHandler.BuildSmartDefaultBucket(fiftyChar).Should().BeNull();
    }

    [Fact]
    public void BuildSmartDefaultBucket_AcceptsRealisticProjectIds()
    {
        SettingsCommandHandler.BuildSmartDefaultBucket("my-startup-123456")
            .Should().Be("my-startup-123456-wirecopy-feed");
        SettingsCommandHandler.BuildSmartDefaultBucket("acmecorp-podcasts-prod")
            .Should().Be("acmecorp-podcasts-prod-wirecopy-feed");
    }
}
