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

    /// <inheritdoc />
    public string BucketName => _config.BucketName ?? string.Empty;

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
            return ServiceAccountKeyValidationResult.Invalid(
                $"File not found at {path}. If you meant to paste the JSON contents, paste them directly — you don't need a path.");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return ServiceAccountKeyValidationResult.Invalid($"Couldn't read {path}: {ex.Message}");
        }

        // Defer to the content validator — same error messages, single source of truth.
        return ValidateKeyContent(json);
    }

    /// <summary>
    /// Validates JSON content as a service account key without requiring a file
    /// on disk. Error messages are written for end-users (workspace-x7lf) — they
    /// describe the next concrete action the user should take rather than
    /// surfacing parser internals.
    /// </summary>
    public static ServiceAccountKeyValidationResult ValidateKeyContent(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            return ServiceAccountKeyValidationResult.Invalid(
                "Nothing pasted. Copy the JSON file's contents and try again — Ctrl+V then Enter.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ServiceAccountKeyValidationResult.Invalid(
                "That doesn't look like JSON. Paste the full file contents including the opening { and closing }.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ServiceAccountKeyValidationResult.Invalid(
                    "That doesn't look like JSON. Paste the full file contents including the opening { and closing }.");
            }

            // Check `type` first so we can give the most specific error
            // (wrong-credential-type vs. missing-fields). Only fire the
            // wrong-type message when `type` is a present, non-empty string
            // that names some other credential — null/missing/empty falls
            // through to the missing-field path below for a clearer error.
            if (root.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String)
            {
                var actualType = typeProp.GetString();
                if (!string.IsNullOrWhiteSpace(actualType)
                    && !string.Equals(actualType, "service_account", StringComparison.Ordinal))
                {
                    return ServiceAccountKeyValidationResult.Invalid(
                        $"This looks like a {actualType} credential, not a service account. " +
                        "In the Keys tab of a service account, click Add Key → Create new key → JSON.");
                }
            }

            foreach (var field in RequiredKeyFileFields)
            {
                if (!root.TryGetProperty(field, out var prop)
                    || prop.ValueKind == JsonValueKind.Null
                    || (prop.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(prop.GetString())))
                {
                    return ServiceAccountKeyValidationResult.Invalid(MissingFieldMessage(field));
                }
            }

            // Soft sanity check on the private key — catches terminals that
            // mangled multi-line input. Accept any PEM marker (PKCS8, PKCS1,
            // EC); the runtime credential loader will surface a more specific
            // error if the key body is corrupt.
            if (root.TryGetProperty("private_key", out var pkProp)
                && pkProp.ValueKind == JsonValueKind.String)
            {
                var pk = pkProp.GetString() ?? string.Empty;
                if (!pk.Contains("-----BEGIN", StringComparison.Ordinal)
                    || !pk.Contains("PRIVATE KEY", StringComparison.Ordinal))
                {
                    return ServiceAccountKeyValidationResult.Invalid(
                        "private_key looks malformed. Did your terminal mangle line breaks? Try pasting again; we handle multi-line input.");
                }
            }
        }

        return ServiceAccountKeyValidationResult.Valid();
    }

    /// <summary>
    /// Extracts the service-account email and project ID from a JSON key blob
    /// for display in the Setup screen status row (workspace-x7lf). Returns
    /// (null, null) if the JSON is malformed; callers should treat that as
    /// "configured but display unavailable" and not fail.
    /// </summary>
    public static (string? ClientEmail, string? ProjectId) ExtractKeyMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? email = null;
            string? project = null;
            if (root.TryGetProperty("client_email", out var emailProp) && emailProp.ValueKind == JsonValueKind.String)
            {
                email = emailProp.GetString();
            }

            if (root.TryGetProperty("project_id", out var projectProp) && projectProp.ValueKind == JsonValueKind.String)
            {
                project = projectProp.GetString();
            }

            return (email, project);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Masks a service-account email for status display. Keeps the first 6
    /// characters of the local part and the project segment of the domain.
    /// e.g. <c>svc-wirecopy@my-proj-1234.iam.gserviceaccount.com</c>
    /// → <c>svc-wi…@my-proj-1234</c>. Returns the original string unchanged
    /// if it doesn't look like an email.
    /// </summary>
    public static string MaskServiceAccountEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var atIdx = email.IndexOf('@', StringComparison.Ordinal);
        if (atIdx <= 0)
        {
            return email;
        }

        var local = email[..atIdx];
        var domain = email[(atIdx + 1)..];
        var maskedLocal = local.Length <= 6 ? local : local[..6] + "…";

        // Domain is typically `<project>.iam.gserviceaccount.com` — keep just
        // the project so the row stays short.
        var dotIdx = domain.IndexOf('.', StringComparison.Ordinal);
        var maskedDomain = dotIdx > 0 ? domain[..dotIdx] : domain;

        return $"{maskedLocal}@{maskedDomain}";
    }

    public async Task<string> UploadAsync(
        string localFilePath,
        string objectName,
        string contentType,
        IProgress<long>? progress = null,
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

        // workspace-74zy: surface byte-level upload progress to the orchestrator
        // so the publish phase no longer goes silent for minutes. The Google SDK
        // exposes IUploadProgress via UploadObjectOptions.ProgressChanged; map
        // its BytesSent into the caller's IProgress<long>.
        IProgress<Google.Apis.Upload.IUploadProgress>? sdkProgress = null;
        if (progress is not null)
        {
            sdkProgress = new Progress<Google.Apis.Upload.IUploadProgress>(p =>
            {
                if (p?.Status == Google.Apis.Upload.UploadStatus.Uploading || p?.Status == Google.Apis.Upload.UploadStatus.Completed)
                {
                    progress.Report(p.BytesSent);
                }
            });
        }

        await client.UploadObjectAsync(
            _config.BucketName,
            objectName,
            contentType,
            stream,
            new UploadObjectOptions(),
            cancellationToken,
            sdkProgress).ConfigureAwait(false);

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

    public async Task<long?> GetObjectSizeAsync(
        string objectName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        var client = await GetClientWithDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var obj = await client.GetObjectAsync(_config.BucketName, objectName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (long?)obj.Size;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string GetPublicUrl(string objectName)
    {
        return $"https://storage.googleapis.com/{_config.BucketName}/{objectName}";
    }

    public Task<CloudStorageValidationResult> ValidateConnectionAsync(
        string bucketName,
        CancellationToken cancellationToken = default) =>
        ValidateConnectionAsync(bucketName, _config.CreateBucketIfNotExists, cancellationToken);

    /// <summary>
    /// Validates connection with an explicit override for the auto-create
    /// behaviour. When <paramref name="allowCreate"/> is false the probe
    /// reports <see cref="CloudStorageValidationErrorType.BucketNotFound"/>
    /// instead of silently creating the bucket — used by Setup's
    /// probe-then-confirm flow (workspace-dwgl) so creation is always an
    /// explicit user choice.
    /// </summary>
    public async Task<CloudStorageValidationResult> ValidateConnectionAsync(
        string bucketName,
        bool allowCreate,
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
                if (allowCreate)
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
    /// Explicitly creates a bucket with the project's configured location,
    /// Standard storage class, and Uniform Bucket-Level Access enabled. Used
    /// by the Setup screen's "Create it for us" flow (workspace-dwgl) which
    /// runs the probe first and only invokes creation on explicit confirm.
    /// </summary>
    public async Task CreateBucketAsync(
        string bucketName,
        string location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bucketName);
        ArgumentNullException.ThrowIfNull(location);

        if (string.IsNullOrWhiteSpace(_config.ProjectId))
        {
            throw new InvalidOperationException(
                "GCP Project ID is required to create buckets. Set 'Gcs:ProjectId' in configuration.");
        }

        var client = await GetClientWithDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

        await client.CreateBucketAsync(
            _config.ProjectId,
            new Google.Apis.Storage.v1.Data.Bucket
            {
                Name = bucketName,
                Location = location,
                StorageClass = "STANDARD",
                IamConfiguration = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData
                {
                    UniformBucketLevelAccess = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData.UniformBucketLevelAccessData
                    {
                        Enabled = true,
                    },
                },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Created bucket {Bucket} in {Location}", bucketName, location);
        _ensuredBucketName = bucketName;
    }

    /// <summary>
    /// Adds the <c>allUsers:roles/storage.objectViewer</c> IAM binding to
    /// <paramref name="bucketName"/> if it isn't already granted. Used by the
    /// publish-failure auto-remediation flow (workspace-p1px) when the post-
    /// upload public HTTP GET returns 403, indicating the bucket is private.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="MakeBucketPublicStatus.AlreadyPublic"/> when the
    /// binding is already present (no API write attempted), <c>Success</c>
    /// after a successful <c>SetBucketIamPolicyAsync</c>, and
    /// <c>PermissionDenied</c> when the service account lacks
    /// <c>storage.buckets.setIamPolicy</c>. Any other error is mapped to
    /// <c>OtherError</c> with the underlying message preserved. Logs at INFO
    /// on success and WARN on failure so operators can trace what happened
    /// to a previously-private bucket without enabling debug logs.
    /// </remarks>
    public async Task<MakeBucketPublicResult> MakeBucketPublicAsync(
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bucketName);

        StorageClient client;
        try
        {
            client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MakeBucketPublicAsync: failed to initialize GCS client for {Bucket}", bucketName);
            return MakeBucketPublicResult.OtherError(ex.Message);
        }

        Google.Apis.Storage.v1.Data.Policy currentPolicy;
        try
        {
            currentPolicy = await client.GetBucketIamPolicyAsync(bucketName, options: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(
                "MakeBucketPublicAsync: SA lacks getIamPolicy on {Bucket} ({Status}): {Error}",
                bucketName,
                ex.HttpStatusCode,
                ex.Message);
            return MakeBucketPublicResult.PermissionDenied(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MakeBucketPublicAsync: GetBucketIamPolicyAsync failed for {Bucket}", bucketName);
            return MakeBucketPublicResult.OtherError(ex.Message);
        }

        if (GcsBucketPublicReadHelper.IsPublicRead(currentPolicy))
        {
            _logger.LogInformation(
                "MakeBucketPublicAsync: {Bucket} already grants allUsers:objectViewer — no change",
                bucketName);
            return MakeBucketPublicResult.AlreadyPublic();
        }

        var updatedPolicy = GcsBucketPublicReadHelper.AddPublicReadBinding(currentPolicy);

        try
        {
            await client.SetBucketIamPolicyAsync(bucketName, updatedPolicy, options: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "MakeBucketPublicAsync: added allUsers:objectViewer to {Bucket}",
                bucketName);
            return MakeBucketPublicResult.Success();
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(
                "MakeBucketPublicAsync: SA lacks setIamPolicy on {Bucket} ({Status}): {Error}",
                bucketName,
                ex.HttpStatusCode,
                ex.Message);
            return MakeBucketPublicResult.PermissionDenied(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MakeBucketPublicAsync: SetBucketIamPolicyAsync failed for {Bucket}", bucketName);
            return MakeBucketPublicResult.OtherError(ex.Message);
        }
    }

    /// <summary>
    /// Best-effort lookup of bucket display metadata (location and the
    /// project ID configured for this client). Returns nulls when the bucket
    /// can't be read; the Setup success panel uses this purely for echo so
    /// missing data is non-fatal.
    /// </summary>
    public async Task<(string? Location, string? ProjectId)> GetBucketInfoAsync(
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bucketName);

        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var bucket = await client.GetBucketAsync(bucketName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (bucket.Location, _config.ProjectId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "GetBucketInfoAsync failed for {Bucket}", bucketName);
            return (null, _config.ProjectId);
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

    /// <summary>
    /// Runs the four-step verify probe (Auth → Upload → Download+compare →
    /// Delete) against <paramref name="bucketName"/>. workspace-cgnt:
    /// replaces the prior GET-bucket-only check so we exercise the actual
    /// permission set the podcast publisher needs at runtime, surfacing
    /// IAM / billing / region failures BEFORE the user proceeds.
    /// </summary>
    public Task<GcsVerifyCredentialsResult> VerifyCredentialsAsync(
        string bucketName,
        CancellationToken cancellationToken = default,
        IProgress<GcsVerifyStep>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(bucketName);
        return GcsCredentialVerifier.VerifyAsync(
            new GcsVerifyOps(this),
            bucketName,
            cancellationToken,
            clock: null,
            progress: progress);
    }

    /// <summary>
    /// Internal accessor used by <see cref="GcsVerifyOps"/> to obtain the
    /// underlying StorageClient. Exposed at <c>internal</c> visibility so
    /// the verify adapter can live in its own file (keeps SA1201 happy)
    /// without making the auth path public.
    /// </summary>
    internal Task<StorageClient> GetClientForVerifyAsync(CancellationToken cancellationToken) =>
        GetClientAsync(cancellationToken);

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

#pragma warning disable SA1204 // private helper for ValidateKeyContent kept at end of file for readability.
    /// <summary>
    /// Per-field "missing field" error messages — workspace-x7lf spec gave each
    /// required field its own guidance because the right next action differs
    /// (re-copy vs. re-export from Cloud Console).
    /// </summary>
    private static string MissingFieldMessage(string field) => field switch
    {
        "type" => "Missing field: type. Re-copy the JSON file from Cloud Console; the file looks truncated.",
        "client_email" => "Missing field: client_email. The JSON looks truncated — re-copy the whole file.",
        "private_key" => "Missing field: private_key. Make sure you copied from the first { to the last } of the file.",
        "project_id" => "Missing field: project_id. Re-export the key from Cloud Console.",
        _ => $"Missing field: {field}. Re-copy the JSON file — it looks truncated.",
    };
#pragma warning restore SA1204
}
