// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using System.Net;
using Google;
using WireCopy.Application.DTOs.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Runs the four-step verify probe (Auth → Upload → Download+compare → Delete)
/// and maps any exception to a <see cref="GcsVerifyFailureClass"/>.
/// </summary>
internal static class GcsCredentialVerifier
{
    /// <summary>
    /// Runs the verify probe. Always uploads, then reads back and compares
    /// bytes, then deletes — there is no shortcut path. Each step is
    /// timed and the failure class is decided by mapping the exception
    /// type and HTTP status to <see cref="GcsVerifyFailureClass"/>.
    /// </summary>
    public static async Task<GcsVerifyCredentialsResult> VerifyAsync(
        IGcsVerifyOperations ops,
        string bucketName,
        CancellationToken ct,
        Func<DateTimeOffset>? clock = null,
        IProgress<GcsVerifyStep>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(bucketName);

        clock ??= () => DateTimeOffset.UtcNow;
        var now = clock();
        var ts = now.ToUnixTimeMilliseconds();
        var iso = now.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var objectName = $"wirecopy-verify-{ts}.txt";
        var contentText = $"WireCopy verification at {iso}";
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(contentText);

        var stepDurations = new long[4];

        // ---- Step 1: Auth ----
        progress?.Report(GcsVerifyStep.Auth);
        var authStart = Stopwatch.GetTimestamp();
        try
        {
            var (authFailure, authMessage) = await ops.AuthenticateAsync(ct).ConfigureAwait(false);
            stepDurations[0] = Stopwatch.GetTimestamp() - authStart;
            if (authFailure is GcsVerifyFailureClass cls && cls != GcsVerifyFailureClass.None)
            {
                return Failed(GcsVerifyStep.Auth, cls, RemediationFor(cls), authMessage, stepDurations, objectName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stepDurations[0] = Stopwatch.GetTimestamp() - authStart;
            var cls = ClassifyAuthException(ex);
            return Failed(GcsVerifyStep.Auth, cls, RemediationFor(cls), ex.Message, stepDurations, objectName);
        }

        // ---- Step 2: Upload ----
        progress?.Report(GcsVerifyStep.Upload);
        var uploadStart = Stopwatch.GetTimestamp();
        try
        {
            await ops.UploadAsync(bucketName, objectName, contentBytes, ct).ConfigureAwait(false);
            stepDurations[1] = Stopwatch.GetTimestamp() - uploadStart;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stepDurations[1] = Stopwatch.GetTimestamp() - uploadStart;
            var cls = ClassifyStorageException(ex);
            return Failed(GcsVerifyStep.Upload, cls, RemediationFor(cls), ex.Message, stepDurations, objectName);
        }

        // ---- Step 3: Download + bytewise compare ----
        progress?.Report(GcsVerifyStep.Download);
        var downloadStart = Stopwatch.GetTimestamp();
        try
        {
            var got = await ops.DownloadAsync(bucketName, objectName, ct).ConfigureAwait(false);
            stepDurations[2] = Stopwatch.GetTimestamp() - downloadStart;

            if (!BytewiseEqual(got, contentBytes))
            {
                // We still try to delete the object below — log the mismatch
                // but include a real cleanup pass so the bucket isn't left
                // littered. Step 4 still runs.
                var cleanupStart = Stopwatch.GetTimestamp();
                try
                {
                    await ops.DeleteAsync(bucketName, objectName, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup; the readback failure is the real story.
                }

                stepDurations[3] = Stopwatch.GetTimestamp() - cleanupStart;
                return Failed(
                    GcsVerifyStep.Download,
                    GcsVerifyFailureClass.ReadbackMismatch,
                    RemediationFor(GcsVerifyFailureClass.ReadbackMismatch),
                    $"Wrote {contentBytes.Length} bytes, read back {got?.Length ?? 0}",
                    stepDurations,
                    objectName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stepDurations[2] = Stopwatch.GetTimestamp() - downloadStart;
            var cls = ClassifyStorageException(ex);
            return Failed(GcsVerifyStep.Download, cls, RemediationFor(cls), ex.Message, stepDurations, objectName);
        }

        // ---- Step 4: Delete ----
        progress?.Report(GcsVerifyStep.Delete);
        var deleteStart = Stopwatch.GetTimestamp();
        try
        {
            await ops.DeleteAsync(bucketName, objectName, ct).ConfigureAwait(false);
            stepDurations[3] = Stopwatch.GetTimestamp() - deleteStart;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stepDurations[3] = Stopwatch.GetTimestamp() - deleteStart;
            var cls = ClassifyStorageException(ex);
            return Failed(GcsVerifyStep.Delete, cls, RemediationFor(cls), ex.Message, stepDurations, objectName);
        }

        return new GcsVerifyCredentialsResult
        {
            Success = true,
            FailedAt = GcsVerifyStep.None,
            FailureClass = GcsVerifyFailureClass.None,
            Message = $"Verified read+write to gs://{bucketName} in {TotalMs(stepDurations)} ms.",
            ProbeObjectName = objectName,
            AuthDuration = StopwatchToTimeSpan(stepDurations[0]),
            UploadDuration = StopwatchToTimeSpan(stepDurations[1]),
            DownloadDuration = StopwatchToTimeSpan(stepDurations[2]),
            DeleteDuration = StopwatchToTimeSpan(stepDurations[3]),
        };
    }

    /// <summary>
    /// Maps an exception thrown while building the GCS client (auth) to a
    /// failure class. Service-account JSON parse problems and missing key
    /// files are surfaced specifically; everything else falls through to
    /// <see cref="GcsVerifyFailureClass.Generic"/>.
    /// </summary>
    internal static GcsVerifyFailureClass ClassifyAuthException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            FileNotFoundException => GcsVerifyFailureClass.InvalidServiceAccountJson,
            System.Text.Json.JsonException => GcsVerifyFailureClass.InvalidServiceAccountJson,
            HttpRequestException => GcsVerifyFailureClass.NetworkFailure,
            GoogleApiException g when g.HttpStatusCode == HttpStatusCode.Unauthorized
                => GcsVerifyFailureClass.InvalidServiceAccountJson,
            _ when ex.Message.Contains("Error reading credential file", StringComparison.OrdinalIgnoreCase)
                => GcsVerifyFailureClass.InvalidServiceAccountJson,
            _ when ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                => GcsVerifyFailureClass.InvalidServiceAccountJson,
            _ => GcsVerifyFailureClass.Generic,
        };
    }

    /// <summary>
    /// Maps an exception thrown by upload/download/delete operations to a
    /// specific failure class. Looks at the HTTP status, the error message
    /// substring (for billing/region detection), and falls back to
    /// <see cref="GcsVerifyFailureClass.Generic"/>.
    /// </summary>
    internal static GcsVerifyFailureClass ClassifyStorageException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is HttpRequestException)
        {
            return GcsVerifyFailureClass.NetworkFailure;
        }

        if (ex is GoogleApiException g)
        {
            // GCS surfaces these failures via specific message substrings.
            // billingDisabled, locationConstraint, and project-mismatch
            // checks are all done by string match on the message body.
            var message = g.Message ?? string.Empty;

            if (message.Contains("billingDisabled", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("billing has been disabled", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("billing is disabled", StringComparison.OrdinalIgnoreCase))
            {
                return GcsVerifyFailureClass.BillingDisabled;
            }

            if (message.Contains("locationConstraint", StringComparison.OrdinalIgnoreCase) ||
                (message.Contains("region", StringComparison.OrdinalIgnoreCase) &&
                    message.Contains("not allowed", StringComparison.OrdinalIgnoreCase)))
            {
                return GcsVerifyFailureClass.RegionBlock;
            }

            if (message.Contains("does not match", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("project", StringComparison.OrdinalIgnoreCase))
            {
                return GcsVerifyFailureClass.BucketInWrongProject;
            }

            return g.HttpStatusCode switch
            {
                HttpStatusCode.NotFound => GcsVerifyFailureClass.BucketNotFound,
                HttpStatusCode.Forbidden => GcsVerifyFailureClass.IamMissing,
                HttpStatusCode.Unauthorized => GcsVerifyFailureClass.IamMissing,
                _ => GcsVerifyFailureClass.Generic,
            };
        }

        return GcsVerifyFailureClass.Generic;
    }

    /// <summary>
    /// User-facing remediation copy keyed by failure class. Returned in the
    /// status panel so the user knows the next concrete action.
    /// </summary>
    internal static string RemediationFor(GcsVerifyFailureClass cls) => cls switch
    {
        GcsVerifyFailureClass.InvalidServiceAccountJson =>
            "The service account JSON is invalid or expired. Re-export it from Cloud Console → IAM → Service Accounts → Keys → Add Key → JSON.",
        GcsVerifyFailureClass.IamMissing =>
            "The service account is missing IAM permissions. Grant Storage Object Admin (or Object Creator + Viewer) on the bucket.",
        GcsVerifyFailureClass.BucketInWrongProject =>
            "The bucket exists but in a different GCP project than the service account. Either pick a bucket in the same project or grant the service account access from the bucket's project.",
        GcsVerifyFailureClass.BillingDisabled =>
            "Billing is disabled on this GCP project. Enable billing in the Cloud Console before retrying — uploads cannot succeed without it.",
        GcsVerifyFailureClass.NetworkFailure =>
            "Couldn't reach Google Cloud Storage. Check your internet connection and any firewall / VPN rules.",
        GcsVerifyFailureClass.RegionBlock =>
            "The bucket's region or organization policy blocked the upload. Try a different region or check organization policy constraints.",
        GcsVerifyFailureClass.ReadbackMismatch =>
            "Uploaded bytes didn't match what we read back. This is rare and usually indicates a proxy or encryption-at-rest misconfiguration.",
        GcsVerifyFailureClass.BucketNotFound =>
            "Bucket not found. Set the bucket name in Setup or create the bucket first.",
        GcsVerifyFailureClass.Generic =>
            "Verification failed. Check the diagnostic message and the GCS bucket / service account configuration.",
        GcsVerifyFailureClass.None => string.Empty,
        _ => "Verification failed.",
    };

    private static GcsVerifyCredentialsResult Failed(
        GcsVerifyStep step,
        GcsVerifyFailureClass cls,
        string remediation,
        string? diagnostic,
        long[] timings,
        string objectName)
    {
        return new GcsVerifyCredentialsResult
        {
            Success = false,
            FailedAt = step,
            FailureClass = cls,
            Message = remediation,
            Diagnostic = diagnostic,
            ProbeObjectName = objectName,
            AuthDuration = StopwatchToTimeSpan(timings[0]),
            UploadDuration = StopwatchToTimeSpan(timings[1]),
            DownloadDuration = StopwatchToTimeSpan(timings[2]),
            DeleteDuration = StopwatchToTimeSpan(timings[3]),
        };
    }

    private static bool BytewiseEqual(byte[]? a, byte[] b)
    {
        if (a == null || a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static TimeSpan StopwatchToTimeSpan(long ticks)
    {
        if (ticks <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    private static long TotalMs(long[] timings)
    {
        long total = 0;
        for (var i = 0; i < timings.Length; i++)
        {
            total += timings[i];
        }

        return (long)((double)total * 1000.0 / Stopwatch.Frequency);
    }
}
