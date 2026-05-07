// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

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
        result.ErrorMessage.Should().Contain("doesn't look like JSON");
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
        result.ErrorMessage.Should().Contain("Missing field");
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
        result.ErrorMessage.Should().Contain("authorized_user");
        result.ErrorMessage.Should().Contain("service account");
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
        result.ErrorMessage.Should().Contain("doesn't look like JSON");
    }

    [Fact]
    public void ValidateKeyContent_EmptyObject_ReturnsInvalid()
    {
        var result = GcsStorageClient.ValidateKeyContent("{}");

        result.IsValid.Should().BeFalse();
        // Empty object lacks `type` first → reported as missing-field per
        // workspace-x7lf's user-friendly error ordering.
        result.ErrorMessage.Should().Contain("Missing field");
    }

    [Fact]
    public void ValidateKeyContent_NullFieldValues_ReturnsInvalid()
    {
        var json = """{"type": null, "project_id": "p", "client_email": "e", "private_key": "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n"}""";

        var result = GcsStorageClient.ValidateKeyContent(json);

        result.IsValid.Should().BeFalse();
        // null `type` is treated as missing rather than wrong-type.
        result.ErrorMessage.Should().Contain("Missing field: type");
    }

    [Fact]
    public void ValidateKeyContent_WhitespaceFieldValues_ReturnsInvalid()
    {
        var json = """{"type": "service_account", "project_id": "  ", "client_email": "e", "private_key": "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n"}""";

        var result = GcsStorageClient.ValidateKeyContent(json);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Missing field: project_id");
    }

    [Fact]
    public void ValidateKeyContent_EmptyInput_ReturnsNothingPasted()
    {
        var result = GcsStorageClient.ValidateKeyContent(string.Empty);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Nothing pasted");
    }

    [Fact]
    public void ValidateKeyContent_MalformedPrivateKey_ReturnsInvalid()
    {
        // No PEM markers — common terminal-mangle failure mode.
        var json = """{"type": "service_account", "project_id": "p", "client_email": "e", "private_key": "abcdef"}""";

        var result = GcsStorageClient.ValidateKeyContent(json);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("private_key looks malformed");
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

    #region ExtractKeyMetadata + MaskServiceAccountEmail — display helpers (workspace-x7lf)

    [Fact]
    public void ExtractKeyMetadata_ValidJson_ReturnsEmailAndProject()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "my-proj-1234",
            client_email = "wirecopy-svc@my-proj-1234.iam.gserviceaccount.com",
            private_key = "x",
        });

        var (email, project) = GcsStorageClient.ExtractKeyMetadata(json);

        email.Should().Be("wirecopy-svc@my-proj-1234.iam.gserviceaccount.com");
        project.Should().Be("my-proj-1234");
    }

    [Fact]
    public void ExtractKeyMetadata_MalformedJson_ReturnsNullPair()
    {
        var (email, project) = GcsStorageClient.ExtractKeyMetadata("{not json");

        email.Should().BeNull();
        project.Should().BeNull();
    }

    [Fact]
    public void ExtractKeyMetadata_EmptyString_ReturnsNullPair()
    {
        var (email, project) = GcsStorageClient.ExtractKeyMetadata(string.Empty);

        email.Should().BeNull();
        project.Should().BeNull();
    }

    [Fact]
    public void ExtractKeyMetadata_MissingFields_ReturnsAvailableValues()
    {
        var json = """{"client_email": "foo@bar.com"}""";

        var (email, project) = GcsStorageClient.ExtractKeyMetadata(json);

        email.Should().Be("foo@bar.com");
        project.Should().BeNull();
    }

    [Fact]
    public void MaskServiceAccountEmail_NormalEmail_KeepsLocalPrefixAndProject()
    {
        var masked = GcsStorageClient.MaskServiceAccountEmail(
            "wirecopy-svc@my-proj-1234.iam.gserviceaccount.com");

        masked.Should().Be("wireco…@my-proj-1234");
    }

    [Fact]
    public void MaskServiceAccountEmail_ShortLocalPart_NoEllipsis()
    {
        // 6-char local part — exactly the threshold; not truncated.
        var masked = GcsStorageClient.MaskServiceAccountEmail(
            "abcdef@my-proj.iam.gserviceaccount.com");

        masked.Should().Be("abcdef@my-proj");
    }

    [Fact]
    public void MaskServiceAccountEmail_NoAtSign_ReturnedUnchanged()
    {
        var masked = GcsStorageClient.MaskServiceAccountEmail("no-at-sign-here");

        masked.Should().Be("no-at-sign-here");
    }

    [Fact]
    public void MaskServiceAccountEmail_DomainWithoutDot_KeepsFullDomain()
    {
        var masked = GcsStorageClient.MaskServiceAccountEmail("svc-foo@localhost");

        masked.Should().Be("svc-fo…@localhost");
    }

    [Fact]
    public void MaskServiceAccountEmail_EmptyInput_ReturnsEmpty()
    {
        GcsStorageClient.MaskServiceAccountEmail(string.Empty).Should().BeEmpty();
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
