// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

/// <summary>
/// Smoke tests verifying GcsStorageClient produces clear errors under various
/// credential configurations rather than crashing with opaque exceptions.
/// </summary>
[Trait("Category", "Unit")]
public class GcsCredentialSmokeTests : IDisposable
{
    private readonly IUserSettingsStore _settingsStore;
    private readonly List<string> _tempFiles = [];

    public GcsCredentialSmokeTests()
    {
        _settingsStore = Substitute.For<IUserSettingsStore>();
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }

        GC.SuppressFinalize(this);
    }

    #region Client Creation Errors

    [Fact]
    public async Task GetClientAsync_ThrowsClearError_WhenNoKeyConfigured()
    {
        // No ServiceAccountKeyPath in config and no key path in settings store
        // → GetClientAsync throws InvalidOperationException with "not configured"
        var client = CreateClient(serviceAccountKeyPath: null);

        var act = () => client.UploadStringAsync("test", "obj", "text/plain");

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("not configured");
    }

    [Fact]
    public async Task GetClientAsync_ThrowsClearError_WhenKeyFileNotFound()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.json");

        // Return the non-existent path from settings store
        _settingsStore.Get("GcsServiceAccountKeyPath").Returns(nonExistentPath);

        var client = CreateClient(serviceAccountKeyPath: null);

        var act = () => client.UploadStringAsync("test", "obj", "text/plain");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    #endregion

    #region Key File Validation

    [Fact]
    public void ValidateKeyFile_ReturnsError_WhenNotJson()
    {
        var tempFile = CreateTempFile("This is not JSON at all, just plain text.");

        var result = GcsStorageClient.ValidateKeyFile(tempFile);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not valid JSON");
    }

    [Fact]
    public void ValidateKeyFile_ReturnsError_WhenMissingFields()
    {
        // Valid JSON but missing required service account fields (type, project_id, client_email, private_key)
        var incompleteJson = """{"some_field": "value", "another": 42}""";
        var tempFile = CreateTempFile(incompleteJson);

        var result = GcsStorageClient.ValidateKeyFile(tempFile);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("missing");
    }

    [Fact]
    public void ValidateKeyFile_Succeeds_WhenValidServiceAccountKey()
    {
        var tempFile = CreateTempFile(ValidServiceAccountKeyJson);

        var result = GcsStorageClient.ValidateKeyFile(tempFile);

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    #endregion

    #region Key Content Validation

    [Fact]
    public void ValidateKeyContent_ReturnsError_WhenNotJson()
    {
        var result = GcsStorageClient.ValidateKeyContent("This is not JSON");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not valid JSON");
    }

    [Fact]
    public void ValidateKeyContent_ReturnsError_WhenMissingFields()
    {
        var result = GcsStorageClient.ValidateKeyContent("""{"some_field": "value"}""");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("missing");
    }

    [Fact]
    public void ValidateKeyContent_Succeeds_WhenValidServiceAccountKey()
    {
        var result = GcsStorageClient.ValidateKeyContent(ValidServiceAccountKeyJson);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateKeyContent_ReturnsError_WhenWrongType()
    {
        var json = """
            {
              "type": "authorized_user",
              "project_id": "test",
              "client_email": "test@test.iam.gserviceaccount.com",
              "private_key": "key"
            }
            """;

        var result = GcsStorageClient.ValidateKeyContent(json);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("service_account");
    }

    [Fact]
    public void SetServiceAccountKeyContent_SavesFileAndSetsPath()
    {
        var client = CreateClient(serviceAccountKeyPath: null);

        var result = client.SetServiceAccountKeyContent(ValidServiceAccountKeyJson);

        result.IsValid.Should().BeTrue();

        // Should have saved the key path to settings store
        _settingsStore.Received().Set(
            "GcsServiceAccountKeyPath",
            Arg.Is<string>(p => p.EndsWith("gcs-key.json")),
            encrypt: true);

        // Clean up the saved file
        var expectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermReader", "gcs-key.json");
        if (File.Exists(expectedPath))
        {
            File.Delete(expectedPath);
        }
    }

    [Fact]
    public void SetServiceAccountKeyContent_RejectsInvalidJson()
    {
        var client = CreateClient(serviceAccountKeyPath: null);

        var result = client.SetServiceAccountKeyContent("not json");

        result.IsValid.Should().BeFalse();
        _settingsStore.DidNotReceive().Set(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    #endregion

    #region Settings Store Integration

    [Fact]
    public void SetServiceAccountKeyPath_StoresEncrypted()
    {
        var keyFile = CreateTempFile(ValidServiceAccountKeyJson);
        var client = CreateClient(serviceAccountKeyPath: null);

        var result = client.SetServiceAccountKeyPath(keyFile);

        result.IsValid.Should().BeTrue();
        _settingsStore.Received(1).Set("GcsServiceAccountKeyPath", keyFile, encrypt: true);
    }

    [Fact]
    public void ClearServiceAccountKey_RemovesFromSettings()
    {
        var keyFile = CreateTempFile(ValidServiceAccountKeyJson);
        var client = CreateClient(serviceAccountKeyPath: null);

        // Set a key path first
        client.SetServiceAccountKeyPath(keyFile);

        // Clear it
        client.ClearServiceAccountKey();

        _settingsStore.Received(1).Remove("GcsServiceAccountKeyPath");

        // After clearing, GetServiceAccountKeyPath returns null (no settings, no config)
        _settingsStore.Get("GcsServiceAccountKeyPath").Returns((string?)null);
        var path = client.GetServiceAccountKeyPath();
        path.Should().BeNull();
    }

    #endregion

    #region Bucket Validation

    [Fact]
    public async Task BucketValidation_ThrowsClearError_WhenKeyNotConfigured()
    {
        // No key configured → ValidateConnectionAsync returns CredentialsInvalid
        var client = CreateClient(serviceAccountKeyPath: null);

        var result = await client.ValidateConnectionAsync("test-bucket");

        result.IsValid.Should().BeFalse();
        result.ErrorType.Should().Be(CloudStorageValidationErrorType.CredentialsInvalid);
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("not configured");
    }

    #endregion

    #region Helpers

    private const string ValidServiceAccountKeyJson = """
        {
          "type": "service_account",
          "project_id": "test-project",
          "private_key_id": "key-id",
          "private_key": "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA2Z3qX2BTLS4e\n-----END RSA PRIVATE KEY-----\n",
          "client_email": "test@test-project.iam.gserviceaccount.com",
          "client_id": "123456789",
          "auth_uri": "https://accounts.google.com/o/oauth2/auth",
          "token_uri": "https://oauth2.googleapis.com/token"
        }
        """;

    private GcsStorageClient CreateClient(string? serviceAccountKeyPath = null, string? bucketName = null)
    {
        var config = Options.Create(new GcsConfiguration
        {
            BucketName = bucketName ?? "test-bucket",
            ServiceAccountKeyPath = serviceAccountKeyPath,
            CreateBucketIfNotExists = false,
        });

        return new GcsStorageClient(config, _settingsStore, NullLogger<GcsStorageClient>.Instance);
    }

    private string CreateTempFile(string content)
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, content);
        _tempFiles.Add(tempFile);
        return tempFile;
    }

    #endregion
}
