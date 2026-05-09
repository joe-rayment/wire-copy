// Licensed under the MIT License. See LICENSE in the repository root.

using Google.Cloud.Storage.V1;
using WireCopy.Application.DTOs.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Adapts <see cref="GcsStorageClient"/> to <see cref="IGcsVerifyOperations"/>
/// so <see cref="GcsCredentialVerifier"/> can drive the four steps without
/// coupling directly to the Google StorageClient. workspace-cgnt: kept in
/// its own file so the SA1201 ordering rule on GcsStorageClient stays
/// happy.
/// </summary>
internal sealed class GcsVerifyOps : IGcsVerifyOperations
{
    private readonly GcsStorageClient _outer;

    public GcsVerifyOps(GcsStorageClient outer)
    {
        ArgumentNullException.ThrowIfNull(outer);
        _outer = outer;
    }

    public async Task<(GcsVerifyFailureClass? Failure, string? Message)> AuthenticateAsync(CancellationToken ct)
    {
        try
        {
            _ = await _outer.GetClientForVerifyAsync(ct).ConfigureAwait(false);
            return (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (GcsCredentialVerifier.ClassifyAuthException(ex), ex.Message);
        }
    }

    public async Task UploadAsync(string bucket, string objectName, byte[] content, CancellationToken ct)
    {
        var client = await _outer.GetClientForVerifyAsync(ct).ConfigureAwait(false);
        using var stream = new MemoryStream(content);
        await client.UploadObjectAsync(bucket, objectName, "text/plain", stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadAsync(string bucket, string objectName, CancellationToken ct)
    {
        var client = await _outer.GetClientForVerifyAsync(ct).ConfigureAwait(false);
        using var stream = new MemoryStream();
        await client.DownloadObjectAsync(bucket, objectName, stream, cancellationToken: ct).ConfigureAwait(false);
        return stream.ToArray();
    }

    public async Task DeleteAsync(string bucket, string objectName, CancellationToken ct)
    {
        var client = await _outer.GetClientForVerifyAsync(ct).ConfigureAwait(false);
        await client.DeleteObjectAsync(bucket, objectName, cancellationToken: ct).ConfigureAwait(false);
    }
}
