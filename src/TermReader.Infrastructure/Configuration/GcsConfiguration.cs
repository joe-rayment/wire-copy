// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;

namespace TermReader.Infrastructure.Configuration;

/// <summary>
/// Configuration for Google Cloud Storage integration.
/// </summary>
public partial class GcsConfiguration
{
    public const string SectionName = "Gcs";

    /// <summary>
    /// Gets the GCS bucket name for storing podcast files.
    /// Nullable; checked at runtime when publishing is requested.
    /// </summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// Gets the path to a GCS service account key JSON file (from config file).
    /// At runtime, the key path stored in UserSettingsStore takes precedence.
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

    /// <summary>
    /// Validates a bucket name against GCS naming rules.
    /// </summary>
    /// <returns>True if the name is valid, false otherwise.</returns>
    public static bool IsValidBucketName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || name.Length > 63)
        {
            return false;
        }

        return BucketNamePattern().IsMatch(name);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketNamePattern();
}
