// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Classifies the outcome of an attempt to add the
/// <c>allUsers:roles/storage.objectViewer</c> binding to a GCS bucket
/// (workspace-p1px). The auto-remediation flow on a 403-on-public-GET
/// branches on this value: <see cref="Success"/> or <see cref="AlreadyPublic"/>
/// → retry the public reachability probe; <see cref="PermissionDenied"/> →
/// surface a copyable <c>gsutil iam ch</c> one-liner; <see cref="OtherError"/>
/// → keep the original publish-failed verdict.
/// </summary>
public enum MakeBucketPublicStatus
{
    /// <summary>The binding was missing and was successfully added.</summary>
    Success,

    /// <summary>The binding already existed — no API change was made.</summary>
    AlreadyPublic,

    /// <summary>
    /// The service account lacks <c>storage.buckets.setIamPolicy</c>
    /// (typically because it has only <c>Storage Object Admin</c> rather than
    /// <c>Storage Admin</c>). The user must run the gsutil one-liner manually.
    /// </summary>
    PermissionDenied,

    /// <summary>Any other transient or unexpected failure.</summary>
    OtherError,
}

/// <summary>
/// Typed result of <c>GcsStorageClient.MakeBucketPublicAsync</c>. The
/// <see cref="Status"/> field is the load-bearing field for branching;
/// <see cref="ErrorMessage"/> is populated only for <c>PermissionDenied</c>
/// or <c>OtherError</c> and is suitable for INFO/WARN logging or display.
/// </summary>
public sealed record MakeBucketPublicResult(
    MakeBucketPublicStatus Status,
    string? ErrorMessage = null)
{
    public static MakeBucketPublicResult Success() => new(MakeBucketPublicStatus.Success);

    public static MakeBucketPublicResult AlreadyPublic() => new(MakeBucketPublicStatus.AlreadyPublic);

    public static MakeBucketPublicResult PermissionDenied(string message) =>
        new(MakeBucketPublicStatus.PermissionDenied, message);

    public static MakeBucketPublicResult OtherError(string message) =>
        new(MakeBucketPublicStatus.OtherError, message);
}
