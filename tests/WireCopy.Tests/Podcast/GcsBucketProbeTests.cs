// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Smoke tests for the workspace-dwgl probe / create wrapper. Hitting actual
/// GCP would require credentials, so these focus on the wrapper contract:
/// the probe routes through the no-auto-create code path and surfaces the
/// underlying client's error type.
/// </summary>
[Trait("Category", "Unit")]
public class GcsBucketProbeTests
{
    [Fact]
    public async Task ProbeAsync_WithoutCredentials_ReturnsCredentialsInvalid()
    {
        // No service-account key configured anywhere — the client throws
        // InvalidOperationException during GetClientAsync; ValidateConnectionAsync
        // wraps that as CredentialsInvalid.
        var config = Options.Create(new GcsConfiguration
        {
            BucketName = "test-bucket",
            CreateBucketIfNotExists = true, // probe must override this to false
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get(Arg.Any<string>()).Returns((string?)null);
        var sut = new GcsStorageClient(config, settingsStore, NullLogger<GcsStorageClient>.Instance);

        var result = await GcsBucketProbe.ProbeAsync(sut, "test-bucket");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.CredentialsInvalid);
    }

    [Fact]
    public async Task CreateAsync_NullClient_Throws()
    {
        var config = new GcsConfiguration();
        var act = () => GcsBucketProbe.CreateAsync(null!, "bucket", config);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ProbeAsync_NullClient_Throws()
    {
        var act = () => GcsBucketProbe.ProbeAsync(null!, "bucket");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateAsync_NoProjectId_Throws()
    {
        var config = Options.Create(new GcsConfiguration
        {
            BucketName = "test-bucket",
            ProjectId = null,
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get(Arg.Any<string>()).Returns((string?)null);
        var sut = new GcsStorageClient(config, settingsStore, NullLogger<GcsStorageClient>.Instance);

        var act = () => GcsBucketProbe.CreateAsync(sut, "test-bucket", config.Value);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Project ID*");
    }
}
