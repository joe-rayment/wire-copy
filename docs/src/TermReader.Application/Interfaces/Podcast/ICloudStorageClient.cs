// Educational and personal use only.

using TermReader.Application.DTOs.Podcast;

namespace TermReader.Application.Interfaces.Podcast;

/// <summary>
/// Client for uploading and downloading files from cloud storage.
/// </summary>
public interface ICloudStorageClient
{
    /// <summary>
    /// Uploads a file to cloud storage.
    /// </summary>
    /// <param name="localFilePath">Path to the local file to upload.</param>
    /// <param name="objectName">The destination object name in storage.</param>
    /// <param name="contentType">The MIME content type of the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The public URL of the uploaded object.</returns>
    Task<string> UploadAsync(
        string localFilePath,
        string objectName,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a string as a text object to cloud storage.
    /// </summary>
    /// <param name="content">The string content to upload.</param>
    /// <param name="objectName">The destination object name in storage.</param>
    /// <param name="contentType">The MIME content type (e.g., "application/rss+xml").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The public URL of the uploaded object.</returns>
    Task<string> UploadStringAsync(
        string content,
        string objectName,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads an object from cloud storage as a string.
    /// </summary>
    /// <param name="objectName">The object name to download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content as a string, or null if the object does not exist.</returns>
    Task<string?> DownloadStringAsync(
        string objectName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether an object exists in cloud storage.
    /// </summary>
    /// <param name="objectName">The object name to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the object exists.</returns>
    Task<bool> ExistsAsync(
        string objectName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the public URL for an object in cloud storage.
    /// </summary>
    /// <param name="objectName">The object name.</param>
    /// <returns>The public URL of the object.</returns>
    string GetPublicUrl(string objectName);

    /// <summary>
    /// Validates the cloud storage connection by checking credentials, bucket access, and write permissions.
    /// </summary>
    /// <param name="bucketName">The bucket name to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validation result indicating success or the type of failure.</returns>
    Task<CloudStorageValidationResult> ValidateConnectionAsync(
        string bucketName,
        CancellationToken cancellationToken = default);
}
