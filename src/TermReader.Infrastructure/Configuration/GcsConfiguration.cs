// Educational and personal use only.

namespace TermReader.Infrastructure.Configuration;

/// <summary>
/// Configuration for Google Cloud Storage integration.
/// </summary>
public class GcsConfiguration
{
    public const string SectionName = "Gcs";

    /// <summary>
    /// Gets the GCS bucket name for storing podcast files.
    /// Nullable; checked at runtime when publishing is requested.
    /// </summary>
    public string? BucketName { get; init; }

    /// <summary>
    /// Gets the path to a GCS service account key JSON file.
    /// Nullable; uses Application Default Credentials if not set.
    /// </summary>
    public string? ServiceAccountKeyPath { get; init; }

    /// <summary>
    /// Gets the GCP project ID. Required only when creating a new bucket.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// Gets whether to create the bucket if it does not exist. Default: true.
    /// </summary>
    public bool CreateBucketIfNotExists { get; init; } = true;

    /// <summary>
    /// Gets the bucket location for newly created buckets. Default: US.
    /// </summary>
    public string BucketLocation { get; init; } = "US";
}
