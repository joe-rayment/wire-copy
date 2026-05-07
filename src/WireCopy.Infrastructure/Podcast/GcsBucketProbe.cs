// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Shared probe + create helpers for the GCS bucket Setup flow
/// (workspace-dwgl). Both the unified Setup screen
/// (<see cref="WireCopy.Infrastructure.Browser.CommandHandlers.SettingsCommandHandler"/>)
/// and the podcast first-run wizard
/// (<see cref="WireCopy.Infrastructure.Browser.CommandHandlers.PodcastGcsWizard"/>)
/// route through these methods so the runtime behaviour stays in lockstep.
///
/// <para>
/// The probe deliberately bypasses <see cref="GcsConfiguration.CreateBucketIfNotExists"/>
/// so a NotFound result is reported to callers instead of silently auto-creating —
/// creation is now always an explicit user choice (Setup confirms it; the wizard
/// invokes <see cref="CreateAsync"/> directly to preserve its previous
/// auto-bootstrap behaviour).
/// </para>
/// </summary>
internal static class GcsBucketProbe
{
    /// <summary>
    /// Runs <see cref="GcsStorageClient.ValidateConnectionAsync(string, bool, CancellationToken)"/>
    /// with auto-create suppressed so the result faithfully reports
    /// <see cref="CloudStorageValidationErrorType.BucketNotFound"/> when the
    /// bucket doesn't exist. Use <see cref="CreateAsync"/> to follow up on a
    /// NotFound result when the user has confirmed creation.
    /// </summary>
    public static Task<CloudStorageValidationResult> ProbeAsync(
        GcsStorageClient gcsClient,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gcsClient);
        ArgumentNullException.ThrowIfNull(bucketName);

        return gcsClient.ValidateConnectionAsync(bucketName, allowCreate: false, cancellationToken);
    }

    /// <summary>
    /// Explicitly creates the bucket using the configuration's
    /// <see cref="GcsConfiguration.BucketLocation"/>, Standard storage class,
    /// and Uniform Bucket-Level Access enabled. Throws on failure (callers
    /// render the surfaced <see cref="Google.GoogleApiException"/> verbatim).
    /// </summary>
    public static Task CreateAsync(
        GcsStorageClient gcsClient,
        string bucketName,
        GcsConfiguration config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gcsClient);
        ArgumentNullException.ThrowIfNull(bucketName);
        ArgumentNullException.ThrowIfNull(config);

        return gcsClient.CreateBucketAsync(bucketName, config.BucketLocation, cancellationToken);
    }
}
