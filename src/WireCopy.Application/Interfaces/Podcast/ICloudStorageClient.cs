// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;

namespace WireCopy.Application.Interfaces.Podcast;

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
    /// <param name="progress">
    /// Optional sink for byte-level upload progress. Reports cumulative bytes
    /// sent. Lets callers show a live progress bar during multi-MB uploads
    /// instead of going silent for minutes (workspace-74zy). Implementations
    /// may no-op if the underlying SDK does not surface byte-level signal.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The public URL of the uploaded object.</returns>
    Task<string> UploadAsync(
        string localFilePath,
        string objectName,
        string contentType,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a string as a text object to cloud storage.
    /// </summary>
    /// <param name="content">The string content to upload.</param>
    /// <param name="objectName">The destination object name in storage.</param>
    /// <param name="contentType">The MIME content type (e.g., "application/rss+xml").</param>
    /// <param name="cacheControl">
    /// Optional HTTP cache-control header to apply to the uploaded object. Use
    /// <c>"no-cache, max-age=0"</c> for republishable feed metadata (feed.xml,
    /// manifest.json) so podcast clients see updates immediately rather than
    /// being served stale copies from the GCS edge for up to 60 minutes
    /// (workspace-7m8d). Default <c>null</c> leaves bucket defaults in place,
    /// which is correct for immutable content-addressed audio episodes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The public URL of the uploaded object.</returns>
    Task<string> UploadStringAsync(
        string content,
        string objectName,
        string contentType,
        string? cacheControl = null,
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
    /// Returns the size in bytes of the named object, or null if it does not
    /// exist. Used by the publisher to detect content drift: if the local file
    /// size differs from the remote, the upload-skip optimization is unsafe
    /// because the local content has changed under the same deterministic id
    /// (workspace-z2om).
    /// </summary>
    Task<long?> GetObjectSizeAsync(
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

    /// <summary>
    /// The bucket name the client is configured to operate against. Used by
    /// the publish-failure auto-remediation flow (workspace-p1px) so the
    /// publisher can hand the bucket back to <see cref="MakeBucketPublicAsync"/>
    /// without depending on <c>IOptions&lt;GcsConfiguration&gt;</c> directly.
    /// </summary>
    string BucketName { get; }

    /// <summary>
    /// Adds the public-read IAM binding (e.g. <c>allUsers:roles/storage.objectViewer</c>
    /// on GCS) to <paramref name="bucketName"/> if it isn't already present.
    /// Used by the publish-failure auto-remediation flow (workspace-p1px) when
    /// the post-upload anonymous HTTP GET returns 403, indicating the bucket
    /// is private. Returns <see cref="MakeBucketPublicStatus.AlreadyPublic"/>
    /// when the binding is already granted, <c>Success</c> after a successful
    /// IAM update, <c>PermissionDenied</c> when the credentials lack
    /// <c>setIamPolicy</c>, and <c>OtherError</c> for anything else.
    /// </summary>
    Task<MakeBucketPublicResult> MakeBucketPublicAsync(
        string bucketName,
        CancellationToken cancellationToken = default);
}
