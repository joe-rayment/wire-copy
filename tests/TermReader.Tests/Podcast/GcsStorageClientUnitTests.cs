// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

/// <summary>
/// Unit tests for GcsStorageClient initialization, validation, and error paths.
/// These complement the always-skipped integration tests that require GCS credentials.
/// They exist because mocking ICloudStorageClient previously hid the fact that
/// StorageClient.CreateAsync() throws InvalidOperationException without credentials.
/// These tests exercise the actual exception types and validation logic.
/// </summary>
[Trait("Category", "Unit")]
public class GcsStorageClientUnitTests : IDisposable
{
    private readonly string _tempDir;

    public GcsStorageClientUnitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gcs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort cleanup */ }
    }

    #region ValidateKeyFile — static validation

    [Fact]
    public void ValidateKeyFile_NonExistentPath_ReturnsInvalid()
    {
        var result = GcsStorageClient.ValidateKeyFile("/tmp/does-not-exist-12345.json");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("File not found");
    }

    [Fact]
    public void ValidateKeyFile_NullPath_ThrowsArgumentNull()
    {
        var act = () => GcsStorageClient.ValidateKeyFile(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ValidateKeyFile_InvalidJson_ReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(path, "not json at all {{{");

        var result = GcsStorageClient.ValidateKeyFile(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not valid JSON");
    }

    [Fact]
    public void ValidateKeyFile_MissingRequiredFields_ReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "incomplete.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "test-project",
            // Missing: client_email, private_key
        }));

        var result = GcsStorageClient.ValidateKeyFile(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("missing");
    }

    [Fact]
    public void ValidateKeyFile_WrongType_ReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "wrong-type.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            type = "authorized_user", // Not service_account
            project_id = "test",
            client_email = "test@test.iam.gserviceaccount.com",
            private_key = "-----BEGIN RSA PRIVATE KEY-----\ntest\n-----END RSA PRIVATE KEY-----\n",
        }));

        var result = GcsStorageClient.ValidateKeyFile(path);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("service_account");
    }

    [Fact]
    public void ValidateKeyFile_ValidServiceAccountKey_ReturnsValid()
    {
        var path = Path.Combine(_tempDir, "valid.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "test-project",
            client_email = "test@test.iam.gserviceaccount.com",
            private_key = "-----BEGIN RSA PRIVATE KEY-----\ntest\n-----END RSA PRIVATE KEY-----\n",
        }));

        var result = GcsStorageClient.ValidateKeyFile(path);

        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region ValidateKeyContent — JSON string validation

    [Fact]
    public void ValidateKeyContent_NullInput_ThrowsArgumentNull()
    {
        var act = () => GcsStorageClient.ValidateKeyContent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ValidateKeyContent_InvalidJson_ReturnsInvalid()
    {
        var result = GcsStorageClient.ValidateKeyContent("{not json}}}");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not valid JSON");
    }

    [Fact]
    public void ValidateKeyContent_EmptyObject_ReturnsInvalid()
    {
        var result = GcsStorageClient.ValidateKeyContent("{}");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("missing");
    }

    [Fact]
    public void ValidateKeyContent_NullFieldValues_ReturnsInvalid()
    {
        var json = """{"type": null, "project_id": "p", "client_email": "e", "private_key": "k"}""";

        var result = GcsStorageClient.ValidateKeyContent(json);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("missing 'type'");
    }

    [Fact]
    public void ValidateKeyContent_WhitespaceFieldValues_ReturnsInvalid()
    {
        var json = """{"type": "service_account", "project_id": "  ", "client_email": "e", "private_key": "k"}""";

        var result = GcsStorageClient.ValidateKeyContent(json);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("missing 'project_id'");
    }

    [Fact]
    public void ValidateKeyContent_ValidKey_ReturnsValid()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "test-project",
            client_email = "test@test.iam.gserviceaccount.com",
            private_key = "-----BEGIN RSA PRIVATE KEY-----\ntest\n-----END RSA PRIVATE KEY-----\n",
        });

        var result = GcsStorageClient.ValidateKeyContent(json);

        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region GetClientAsync — initialization error paths (tested via public API)

    [Fact]
    public async Task UploadStringAsync_NoKeyConfigured_ThrowsInvalidOperation()
    {
        var config = Options.Create(new GcsConfiguration { BucketName = "test-bucket" });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get(Arg.Any<string>()).Returns((string?)null);

        var sut = new GcsStorageClient(config, settingsStore, NullLogger<GcsStorageClient>.Instance);

        var act = () => sut.UploadStringAsync("content", "test.xml", "text/xml");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task UploadStringAsync_KeyFileMissing_ThrowsFileNotFound()
    {
        var config = Options.Create(new GcsConfiguration
        {
            BucketName = "test-bucket",
            ServiceAccountKeyPath = "/tmp/nonexistent-key-file-12345.json",
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get(Arg.Any<string>()).Returns((string?)null);

        var sut = new GcsStorageClient(config, settingsStore, NullLogger<GcsStorageClient>.Instance);

        var act = () => sut.UploadStringAsync("content", "test.xml", "text/xml");

        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage("*not found*");
    }

    #endregion

    #region GetPublicUrl — URL formatting

    [Fact]
    public void GetPublicUrl_FormatsCorrectly()
    {
        var config = Options.Create(new GcsConfiguration { BucketName = "my-podcast-bucket" });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        var sut = new GcsStorageClient(config, settingsStore, NullLogger<GcsStorageClient>.Instance);

        var url = sut.GetPublicUrl("feed.xml");

        url.Should().Contain("my-podcast-bucket");
        url.Should().Contain("feed.xml");
        url.Should().StartWith("https://");
    }

    #endregion
}
