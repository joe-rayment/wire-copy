// Educational and personal use only.

using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Google Cloud Storage client for uploading podcast audio and feed files.
/// </summary>
internal sealed class GcsStorageClient : ICloudStorageClient
{
    private readonly GcsConfiguration _config;
    private readonly ILogger<GcsStorageClient> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private StorageClient? _client;
    private string? _ensuredBucketName;

    public GcsStorageClient(IOptions<GcsConfiguration> config, ILogger<GcsStorageClient> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<string> UploadAsync(
        string localFilePath,
        string objectName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localFilePath);
        ArgumentNullException.ThrowIfNull(objectName);

        var client = await GetClientAsync(cancellationToken);
        await EnsureBucketAsync(client, cancellationToken);

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
            cancellationToken);

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

        var client = await GetClientAsync(cancellationToken);
        await EnsureBucketAsync(client, cancellationToken);

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
            cancellationToken);

        return GetPublicUrl(objectName);
    }

    public async Task<string?> DownloadStringAsync(
        string objectName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        var client = await GetClientAsync(cancellationToken);

        try
        {
            using var stream = new MemoryStream();
            await client.DownloadObjectAsync(_config.BucketName, objectName, stream, cancellationToken: cancellationToken);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
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

        var client = await GetClientAsync(cancellationToken);

        try
        {
            await client.GetObjectAsync(_config.BucketName, objectName, cancellationToken: cancellationToken);
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

    private async Task<StorageClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client != null)
        {
            return _client;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_client != null)
            {
                return _client;
            }

            if (!string.IsNullOrWhiteSpace(_config.ServiceAccountKeyPath))
            {
                var credential = GoogleCredential.FromFile(_config.ServiceAccountKeyPath);
                _client = await StorageClient.CreateAsync(credential);
            }
            else
            {
                _client = await StorageClient.CreateAsync();
            }

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

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_ensuredBucketName == currentBucket)
            {
                return;
            }

            try
            {
                await client.GetBucketAsync(currentBucket, cancellationToken: cancellationToken);
                _ensuredBucketName = currentBucket;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Creating bucket {Bucket} in {Location}", currentBucket, _config.BucketLocation);

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
                    cancellationToken: cancellationToken);

                _ensuredBucketName = currentBucket;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }
}
