// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;

namespace WireCopy.Infrastructure.Configuration;

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
    /// Validates a bucket name against GCS naming rules. First-pass lexical
    /// check only — does not catch every GCP rule (e.g. ".." substrings or the
    /// reserved <c>goog</c> prefix). Callers that surface user-facing errors
    /// should layer those extra checks on top (see the bucket Setup row in
    /// SettingsCommandHandler — workspace-dwgl, workspace-wooa).
    /// </summary>
    /// <returns>True if the name is valid, false otherwise.</returns>
    public static bool IsValidBucketName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || name.Length > 63)
        {
            return false;
        }

        // Bucket names with dots must follow DNS rules, which exclude
        // underscores. Names without dots may contain underscores. Names
        // cannot contain BOTH (mixed _ and . is invalid per GCS).
        if (name!.Contains('.') && name.Contains('_'))
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (name.StartsWith("goog", StringComparison.Ordinal))
        {
            return false;
        }

        return BucketNamePattern().IsMatch(name);
    }

    /// <summary>
    /// Returns a user-facing explanation of why a bucket name is invalid, or
    /// <c>null</c> when the name passes validation. Intended for inline error
    /// rendering — surfaces the specific rule that failed instead of dumping
    /// every constraint at once (workspace-wooa).
    /// </summary>
    public static string? ExplainBucketInvalid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Bucket name cannot be empty";
        }

        var s = name!;

        if (s.Length < 3)
        {
            return "Too short — bucket names must be at least 3 characters";
        }

        if (s.Length > 63)
        {
            return "Too long — bucket names must be at most 63 characters";
        }

        if (s != s.ToLowerInvariant())
        {
            return "Use only lowercase letters — uppercase is not allowed";
        }

        if (s.Contains("..", StringComparison.Ordinal))
        {
            return "Bucket names cannot contain two consecutive dots";
        }

        if (s.StartsWith("goog", StringComparison.Ordinal))
        {
            return "Bucket names cannot begin with the reserved \"goog\" prefix";
        }

        if (s.Contains('.') && s.Contains('_'))
        {
            return "Bucket names with dots cannot also contain underscores";
        }

        if (!char.IsLetterOrDigit(s[0]) || !char.IsLetterOrDigit(s[^1]))
        {
            return "Must start and end with a letter or number";
        }

        if (!BucketNamePattern().IsMatch(s))
        {
            return "Use only lowercase a-z, 0-9, hyphens, underscores, dots";
        }

        return null;
    }

    // workspace-wooa: underscores ARE valid in GCS bucket names (per
    // https://cloud.google.com/storage/docs/buckets#naming). The DNS-style
    // restriction only applies to names containing dots; the IsValidBucketName
    // pre-check above rejects mixed dot+underscore names.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketNamePattern();
}
