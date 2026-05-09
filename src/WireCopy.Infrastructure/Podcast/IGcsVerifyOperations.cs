// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Operations the four-step verify flow needs from a GCS-shaped client.
/// Extracted as an interface so unit tests can substitute deterministic
/// fakes and exercise the failure-class mapping without hitting the
/// network. workspace-cgnt: prior attempts at the verify step substituted
/// a GET-bucket-metadata probe and called it verified — this interface
/// makes the four real steps the only way through.
/// </summary>
internal interface IGcsVerifyOperations
{
    /// <summary>
    /// Step 1: Authenticate. Returns null on success, a failure-class plus
    /// raw message on failure (so the verifier can short-circuit before
    /// uploading anything).
    /// </summary>
    Task<(GcsVerifyFailureClass? Failure, string? Message)> AuthenticateAsync(CancellationToken ct);

    /// <summary>Step 2: Upload an object containing the given UTF-8 bytes.</summary>
    Task UploadAsync(string bucket, string objectName, byte[] content, CancellationToken ct);

    /// <summary>Step 3: Download the object and return its bytes.</summary>
    Task<byte[]> DownloadAsync(string bucket, string objectName, CancellationToken ct);

    /// <summary>Step 4: Delete the object.</summary>
    Task DeleteAsync(string bucket, string objectName, CancellationToken ct);
}
