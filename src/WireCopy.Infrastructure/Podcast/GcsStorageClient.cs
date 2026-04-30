// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Google Cloud Storage client for uploading podcast audio and feed files.
/// </summary>
internal sealed class GcsStorageClient : ICloudStorageClient
{
    private const string ServiceAccountKeySettingsKey = "GcsServiceAccountKeyPath";

    private static readonly string[] RequiredKeyFileFields =
        ["type", "project_id", "client_email", "private_key"];

    private readonly GcsConfiguration _config;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<GcsStorageClient> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private StorageClient? _client;
    private string? _cachedKeyPath;
    private string? _ensuredBucketName;

    public GcsStorageClient(
        IOptions<GcsConfiguration> config,
        IUserSettingsStore settingsStore,
        ILogger<GcsStorageClient> logger)
    {
        _config = config.Value;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>
    /// Validates a service account key file without storing it.
    /// </summary>
    /// <param name="path">Absolute path to the service account key JSON file.</param>
    /// <returns>A validation result with clear error messages if invalid.</returns>
    public static ServiceAccountKeyValidationResult ValidateKeyFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            return ServiceAccountKeyValidationResult.Invalid($"File not found: {path}");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return ServiceAccountKeyValidationResult.Invalid($"Cannot read file: {ex.Message}");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ServiceAccountKeyValidationResult.Invalid("File is not valid JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;

            foreach (var field in RequiredKeyFileFields)
            {
                if (!root.TryGetProperty(field, out var prop)
                    || prop.ValueKind == JsonValueKind.Null
                    || (prop.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(prop.GetString())))
                {
                    return ServiceAccountKeyValidationResult.Invalid(
                        $"Invalid service account key file: missing '{field}'");
                }
            }

            if (root.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() != "service_account")
            {
                return ServiceAccountKeyValidationResult.Invalid(
                    $"Invalid key file: 'type' must be 'service_account', got '{typeProp.GetString()}'");
            }
        }

        return ServiceAccountKeyValidationResult.Valid();
    }

    /// <summary>
    /// Validates JSON content as a service account key without requiring a file on disk.
    /// On JSON parse failure, the returned error message includes the parser's
    /// specific complaint (line/position) so the user can see what went wrong.
    /// </summary>
    public static ServiceAccountKeyValidationResult ValidateKeyContent(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // Surface the parser's specific message (e.g. "expected '}' at line 4 pos 12")
            // so the user can fix the actual problem instead of guessing.
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? "Input is not valid JSON" : ex.Message;
            return ServiceAccountKeyValidationResult.Invalid($"Input is not valid JSON: {detail}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            foreach (var field in RequiredKeyFileFields)
            {
                if (!root.TryGetProperty(field, out var prop)
                    || prop.ValueKind == JsonValueKind.Null
                    || (prop.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(prop.GetString())))
                {
                    return ServiceAccountKeyValidationResult.Invalid(
                        $"Invalid service account key: missing '{field}'");
                }
            }

            if (root.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() != "service_account")
            {
                return ServiceAccountKeyValidationResult.Invalid(
                    $"Invalid key: 'type' must be 'service_account', got '{typeProp.GetString()}'");
            }
        }

        return ServiceAccountKeyValidationResult.Valid();
    }

    public async Task<string> UploadAsync(
        string localFilePath,
        string objectName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localFilePath);
        ArgumentNullException.ThrowIfNull(objectName);

        var client = await GetClientWithDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBucketAsync(client, cancellationToken).ConfigureAwait(false);

        await using var stream = File.OpenRead(localFilePath);

        _logger.LogInformation(
            "Uploading {FilePath} to gs://{Bucket}/{Object}",
            localFilePath,
            _config.BucketName,
            objectName);

        await client.UploadObjectAsync(
            _config.BucketName,
            objectName,
            contentType,
            stream,
            new UploadObjectOptions(),
            cancellationToken).ConfigureAwait(false);

        return GetPublicUrl(objectName);
    }

    public async Task<string> UploadStringAsync(
        string content,
        string objectName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(objectName);

        var client = await GetClientWithDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBucketAsync(client, cancellationToken).ConfigureAwait(false);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        _logger.LogDebug(
            "Uploading string content to gs://{Bucket}/{Object}",
            _config.BucketName,
            objectName);

        await client.UploadObjectAsync(
            _config.BucketName,
            objectName,
            contentType,
            stream,
            new UploadObjectOptions(),
            cancellationToken).ConfigureAwait(false);

        return GetPublicUrl(objectName);
    }

    public async Task<string?> DownloadStringAsync(
        string objectName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        var client = await GetClientWithDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var stream = new MemoryStream();
            await client.DownloadObjectAsync(_config.BucketName, objectName, stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(
        string objectName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        var client = await GetClientWithDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await client.GetObjectAsync(_config.BucketName, objectName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public string GetPublicUrl(string objectName)
    {
        return $"https://storage.googleapis.com/{_config.BucketName}/{objectName}";
    }

    public async Task<CloudStorageValidationResult> ValidateConnectionAsync(
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bucketName);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var ct = linkedCts.Token;

        try
        {
            // Step 1: Authenticate - create/reuse StorageClient
            StorageClient client;
            try
            {
                client = await GetClientAsync(ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException ex)
            {
                return CloudStorageValidationResult.Invalid(
                    CloudStorageValidationErrorType.CredentialsInvalid,
                    $"Service account key file not found at: {ex.FileName}");
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized)
            {
                return CloudStorageValidationResult.Invalid(
                    CloudStorageValidationErrorType.CredentialsInvalid,
                    $"Service account authentication failed: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "GCS client creation failed with InvalidOperationException");
                return CloudStorageValidationResult.Invalid(
                    CloudStorageValidationErrorType.CredentialsInvalid,
                    "GCS service account key not configured. Use :set key to set the key file path.");
            }

            // Step 2: Bucket read - verify bucket exists (or create)
            try
            {
                await client.GetBucketAsync(bucketName, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.NotFound)
            {
                if (_config.CreateBucketIfNotExists)
                {
                    if (string.IsNullOrWhiteSpace(_config.ProjectId))
                    {
                        return CloudStorageValidationResult.Invalid(
                            CloudStorageValidationErrorType.BucketCreationFailed,
                            "GCP Project ID is required to create buckets. Set 'Gcs:ProjectId' in configuration.");
                    }

                    try
                    {
                        await client.CreateBucketAsync(
                            _config.ProjectId,
                            new Google.Apis.Storage.v1.Data.Bucket
                            {
                                Name = bucketName,
                                Location = _config.BucketLocation,
                                IamConfiguration = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData
                                {
                                    UniformBucketLevelAccess = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData.UniformBucketLevelAccessData
                                    {
                                        Enabled = true,
                                    },
                                },
                            },
                            cancellationToken: ct).ConfigureAwait(false);

                        _logger.LogInformation("Created bucket {Bucket} during validation", bucketName);
                    }
                    catch (Google.GoogleApiException createEx)
                    {
                        return CloudStorageValidationResult.Invalid(
                            CloudStorageValidationErrorType.BucketCreationFailed,
                            $"Bucket '{bucketName}' not found and creation failed: {createEx.Message}");
                    }
                }
                else
                {
                    return CloudStorageValidationResult.Invalid(
                        CloudStorageValidationErrorType.BucketNotFound,
                        $"Bucket '{bucketName}' not found. Create it first or enable auto-creation.");
                }
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Forbidden)
            {
                return CloudStorageValidationResult.Invalid(
                    CloudStorageValidationErrorType.AccessDenied,
                    $"Access denied to bucket '{bucketName}'. Check IAM permissions.");
            }

            // Step 3: Write probe - upload and delete a sentinel object
            const string probePath = "podcasts/.validation-probe";
            try
            {
                using var probeStream = new MemoryStream("validation-probe"u8.ToArray());
                await client.UploadObjectAsync(
                    bucketName,
                    probePath,
                    "text/plain",
                    probeStream,
                    cancellationToken: ct).ConfigureAwait(false);

                await client.DeleteObjectAsync(bucketName, probePath, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Google.GoogleApiException ex) when (
                ex.HttpStatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                return CloudStorageValidationResult.Invalid(
                    CloudStorageValidationErrorType.AccessDenied,
                    $"Cannot write to bucket '{bucketName}'. Check write permissions.");
            }

            // Update cached state so EnsureBucketAsync skips re-check for this bucket.
            // Caller must also update _config.BucketName for subsequent operations to use the validated bucket.
            _ensuredBucketName = bucketName;

            _logger.LogInformation("Validated GCS connection to bucket {Bucket}", bucketName);
            return CloudStorageValidationResult.Valid();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return CloudStorageValidationResult.Invalid(
                CloudStorageValidationErrorType.Timeout,
                "Connection timed out after 10 seconds. Check network connectivity.");
        }
        catch (HttpRequestException ex)
        {
            return CloudStorageValidationResult.Invalid(
                CloudStorageValidationErrorType.NetworkError,
                $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during GCS validation for bucket {Bucket}", bucketName);
            return CloudStorageValidationResult.Invalid(
                CloudStorageValidationErrorType.NetworkError,
                $"Validation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the service account key file path after validating the file.
    /// The path is stored encrypted in user settings.
    /// </summary>
    /// <param name="path">Absolute path to the service account key JSON file.</param>
    /// <returns>A validation result indicating success or describing the error.</returns>
    public ServiceAccountKeyValidationResult SetServiceAccountKeyPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var validation = ValidateKeyFile(path);
        if (!validation.IsValid)
        {
            return validation;
        }

        _settingsStore.Set(ServiceAccountKeySettingsKey, path, encrypt: true);

        // Invalidate the cached client so the next call picks up the new key
        _client = null;
        _cachedKeyPath = null;
        _ensuredBucketName = null;

        _logger.LogInformation("Service account key path updated");
        return ServiceAccountKeyValidationResult.Valid();
    }

    /// <summary>
    /// Accepts pasted JSON key content, saves it to the app data directory,
    /// and configures the service account key path.
    /// </summary>
    public ServiceAccountKeyValidationResult SetServiceAccountKeyContent(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var validation = ValidateKeyContent(json);
        if (!validation.IsValid)
        {
            return validation;
        }

        // Save to {LocalAppData}/WireCopy/gcs-key.json
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy");
        Directory.CreateDirectory(appData);

        var keyFilePath = Path.Combine(appData, "gcs-key.json");
        File.WriteAllText(keyFilePath, json);

        // Now set the path as usual
        return SetServiceAccountKeyPath(keyFilePath);
    }

    /// <summary>
    /// Returns the stored service account key path, or null if not configured.
    /// </summary>
    public string? GetServiceAccountKeyPath()
    {
        return _settingsStore.Get(ServiceAccountKeySettingsKey)
               ?? _config.ServiceAccountKeyPath;
    }

    /// <summary>
    /// Removes the stored service account key path from settings and invalidates the cached client.
    /// </summary>
    public void ClearServiceAccountKey()
    {
        _settingsStore.Remove(ServiceAccountKeySettingsKey);
        _client = null;
        _cachedKeyPath = null;
        _ensuredBucketName = null;

        _logger.LogInformation("Service account key path cleared");
    }

    private async Task<StorageClient> GetClientWithDiagnosticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "GCS service account key not configured. Use :set key to set the key file path.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new FileNotFoundException(
                $"Service account key file not found at: {ex.FileName}", ex.FileName, ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Invalid service account key file: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Service account authentication failed: {ex.Message}", ex);
        }
    }

    private async Task<StorageClient> GetClientAsync(CancellationToken cancellationToken)
    {
        var keyPath = GetServiceAccountKeyPath();

        // If the key path changed, invalidate the cached client
        if (_client != null && _cachedKeyPath == keyPath)
        {
            return _client;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after lock acquisition
            keyPath = GetServiceAccountKeyPath();
            if (_client != null && _cachedKeyPath == keyPath)
            {
                return _client;
            }

            if (string.IsNullOrWhiteSpace(keyPath))
            {
                throw new InvalidOperationException(
                    "GCS service account key not configured. Use :set key to set the key file path.");
            }

            if (!File.Exists(keyPath))
            {
                throw new FileNotFoundException(
                    $"Service account key file not found: {keyPath}", keyPath);
            }

            var credential = GoogleCredential.FromFile(keyPath);
            _client = await StorageClient.CreateAsync(credential).ConfigureAwait(false);
            _cachedKeyPath = keyPath;

            return _client;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureBucketAsync(StorageClient client, CancellationToken cancellationToken)
    {
        var currentBucket = _config.BucketName;

        if (_ensuredBucketName == currentBucket || !_config.CreateBucketIfNotExists)
        {
            _ensuredBucketName = currentBucket;
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_ensuredBucketName == currentBucket)
            {
                return;
            }

            try
            {
                await client.GetBucketAsync(currentBucket, cancellationToken: cancellationToken).ConfigureAwait(false);
                _ensuredBucketName = currentBucket;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Creating bucket {Bucket} in {Location}", currentBucket, _config.BucketLocation);

                if (string.IsNullOrWhiteSpace(_config.ProjectId))
                {
                    throw new InvalidOperationException(
                        "GCP Project ID is required to create buckets. Set 'Gcs:ProjectId' in configuration.");
                }

                await client.CreateBucketAsync(
                    _config.ProjectId,
                    new Google.Apis.Storage.v1.Data.Bucket
                    {
                        Name = currentBucket,
                        Location = _config.BucketLocation,
                        IamConfiguration = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData
                        {
                            UniformBucketLevelAccess = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData.UniformBucketLevelAccessData
                            {
                                Enabled = true,
                            },
                        },
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                _ensuredBucketName = currentBucket;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }
}
