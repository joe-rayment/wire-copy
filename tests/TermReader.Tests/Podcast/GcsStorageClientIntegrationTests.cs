// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Integration")]
public class GcsStorageClientIntegrationTests : IDisposable
{
    private static readonly string? BucketName = Environment.GetEnvironmentVariable("GCS_TEST_BUCKET");

    private static bool HasGcsCredentials =>
        !string.IsNullOrWhiteSpace(BucketName) &&
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")) ||
         !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GCS_SERVICE_ACCOUNT_KEY")));

    private readonly List<string> _uploadedObjects = [];

    private GcsStorageClient CreateClient()
    {
        var config = Options.Create(new GcsConfiguration
        {
            BucketName = BucketName,
            CreateBucketIfNotExists = false,
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        return new GcsStorageClient(config, settingsStore, NullLogger<GcsStorageClient>.Instance);
    }

    public void Dispose()
    {
        // Note: cleanup of uploaded objects would require delete API
        // For integration tests, use a dedicated test bucket with lifecycle rules
        _uploadedObjects.Clear();
    }

    [Fact(Skip = "Requires GCS credentials and GCS_TEST_BUCKET env var")]
    public async Task UploadStringAsync_ValidContent_ReturnsPublicUrl()
    {
        if (!HasGcsCredentials)
        {
            return;
        }

        var client = CreateClient();
        var objectName = $"test/{Guid.NewGuid():N}.txt";
        var content = "Hello from integration test!";

        var url = await client.UploadStringAsync(content, objectName, "text/plain");

        url.Should().NotBeNullOrEmpty();
        url.Should().Contain(BucketName!);
        _uploadedObjects.Add(objectName);
    }

    [Fact(Skip = "Requires GCS credentials and GCS_TEST_BUCKET env var")]
    public async Task DownloadStringAsync_AfterUpload_MatchesContent()
    {
        if (!HasGcsCredentials)
        {
            return;
        }

        var client = CreateClient();
        var objectName = $"test/{Guid.NewGuid():N}.txt";
        var expectedContent = $"Test content {DateTime.UtcNow:O}";

        await client.UploadStringAsync(expectedContent, objectName, "text/plain");
        _uploadedObjects.Add(objectName);

        var downloaded = await client.DownloadStringAsync(objectName);

        downloaded.Should().Be(expectedContent);
    }

    [Fact(Skip = "Requires GCS credentials and GCS_TEST_BUCKET env var")]
    public async Task ExistsAsync_UploadedObject_ReturnsTrue()
    {
        if (!HasGcsCredentials)
        {
            return;
        }

        var client = CreateClient();
        var objectName = $"test/{Guid.NewGuid():N}.txt";

        await client.UploadStringAsync("exists-test", objectName, "text/plain");
        _uploadedObjects.Add(objectName);

        var exists = await client.ExistsAsync(objectName);

        exists.Should().BeTrue();
    }

    [Fact(Skip = "Requires GCS credentials and GCS_TEST_BUCKET env var")]
    public async Task ExistsAsync_NonExistentObject_ReturnsFalse()
    {
        if (!HasGcsCredentials)
        {
            return;
        }

        var client = CreateClient();
        var objectName = $"test/nonexistent-{Guid.NewGuid():N}.txt";

        var exists = await client.ExistsAsync(objectName);

        exists.Should().BeFalse();
    }

    [Fact(Skip = "Requires GCS credentials and GCS_TEST_BUCKET env var")]
    public void GetPublicUrl_ValidObjectName_ReturnsFormattedUrl()
    {
        if (!HasGcsCredentials)
        {
            return;
        }

        var client = CreateClient();
        var objectName = "test/some-file.xml";

        var url = client.GetPublicUrl(objectName);

        url.Should().Contain("storage.googleapis.com");
        url.Should().Contain(BucketName!);
        url.Should().Contain(objectName);
    }
}
