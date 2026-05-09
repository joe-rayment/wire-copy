// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Outcome of <c>GcsStorageClient.VerifyCredentialsAsync</c> — the four-step
/// real-world probe (Auth, Upload, Download, Delete) introduced by
/// workspace-cgnt to replace the prior GET-bucket-only check. Each step's
/// timing is captured so the live status panel can show a four-line
/// progress display, and on failure the specific <see cref="GcsVerifyFailureClass"/>
/// drives a remediation copy block.
/// </summary>
public sealed record GcsVerifyCredentialsResult
{
    /// <summary>True when all four steps completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>The step that failed, or <see cref="GcsVerifyStep.None"/> on success.</summary>
    public GcsVerifyStep FailedAt { get; init; } = GcsVerifyStep.None;

    /// <summary>Failure category, populated when <see cref="Success"/> is false.</summary>
    public GcsVerifyFailureClass FailureClass { get; init; } = GcsVerifyFailureClass.None;

    /// <summary>User-facing message, populated on success and on failure.</summary>
    public string? Message { get; init; }

    /// <summary>Underlying exception message for diagnostics. Not displayed to users directly.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>Per-step timing — a value of zero means the step was not run.</summary>
    public TimeSpan AuthDuration { get; init; }

    /// <summary>Per-step timing — a value of zero means the step was not run.</summary>
    public TimeSpan UploadDuration { get; init; }

    /// <summary>Per-step timing — a value of zero means the step was not run.</summary>
    public TimeSpan DownloadDuration { get; init; }

    /// <summary>Per-step timing — a value of zero means the step was not run.</summary>
    public TimeSpan DeleteDuration { get; init; }

    /// <summary>Object name written to the bucket during verify (kept for diagnostics).</summary>
    public string? ProbeObjectName { get; init; }
}

/// <summary>
/// Stage of the four-step verify flow. Used by the live status panel to
/// highlight the currently-running step and to mark which step failed.
/// </summary>
public enum GcsVerifyStep
{
    /// <summary>No step has run yet, or the verify completed successfully.</summary>
    None,

    /// <summary>Authenticating against GCS using the service account credential.</summary>
    Auth,

    /// <summary>Uploading the sentinel object.</summary>
    Upload,

    /// <summary>Downloading the sentinel object and comparing bytes.</summary>
    Download,

    /// <summary>Deleting the sentinel object.</summary>
    Delete,
}

/// <summary>
/// Specific failure classes surfaced by VerifyCredentialsAsync. Each class
/// carries its own remediation copy in the panel so the user knows what
/// to fix without reading raw GCS error text.
/// </summary>
public enum GcsVerifyFailureClass
{
    /// <summary>Verify succeeded — no failure.</summary>
    None,

    /// <summary>Service account JSON is malformed or could not be loaded.</summary>
    InvalidServiceAccountJson,

    /// <summary>Service account is valid but lacks IAM permissions.</summary>
    IamMissing,

    /// <summary>Bucket exists but in a different project than the credential's project_id.</summary>
    BucketInWrongProject,

    /// <summary>Bucket project has billing disabled.</summary>
    BillingDisabled,

    /// <summary>Network failure — DNS, connection refused, TLS error.</summary>
    NetworkFailure,

    /// <summary>Region restriction prevents the upload.</summary>
    RegionBlock,

    /// <summary>Downloaded bytes did not match what was uploaded.</summary>
    ReadbackMismatch,

    /// <summary>Bucket does not exist.</summary>
    BucketNotFound,

    /// <summary>Catch-all — see <see cref="GcsVerifyCredentialsResult.Diagnostic"/> for raw text.</summary>
    Generic,
}
