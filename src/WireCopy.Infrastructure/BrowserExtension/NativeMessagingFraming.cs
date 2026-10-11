// Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// The browser's native-messaging wire format (workspace-m7aml): every message is a 4-byte
/// length in the machine's native byte order followed by that many bytes of UTF-8 JSON.
/// The browser caps a message it sends us at 64 MiB and a message we send it at 1 MB.
/// </summary>
internal static class NativeMessagingFraming
{
    /// <summary>The largest message the browser may send us (64 MiB); anything bigger is refused.</summary>
    public const int MaxIncomingBytes = 64 * 1024 * 1024;

    /// <summary>The largest message we may send the browser (1 MB); the browser drops bigger ones.</summary>
    public const int MaxOutgoingBytes = 1024 * 1024;

    /// <summary>Reads one framed message.</summary>
    /// <param name="input">The stream the browser writes to (the host's stdin).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message's JSON text, or null on a clean end of stream before a length prefix.</returns>
    /// <exception cref="NativeMessagingFrameException">
    /// The length prefix exceeds <see cref="MaxIncomingBytes"/> (nothing more is read), or the
    /// stream ended in the middle of a prefix or body.
    /// </exception>
    public static async Task<string?> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var got = await ReadFullyAsync(input, prefix, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < prefix.Length)
        {
            throw new NativeMessagingFrameException("The browser closed the connection in the middle of a message.");
        }

        var length = BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(prefix)
            : BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length > MaxIncomingBytes)
        {
            throw new NativeMessagingFrameException($"The message is too large ({length} bytes; the limit is {MaxIncomingBytes}).");
        }

        var body = new byte[length];
        if (await ReadFullyAsync(input, body, cancellationToken).ConfigureAwait(false) < body.Length)
        {
            throw new NativeMessagingFrameException("The browser closed the connection in the middle of a message.");
        }

        return Encoding.UTF8.GetString(body);
    }

    /// <summary>Writes one framed message and flushes.</summary>
    /// <param name="output">The stream the browser reads from (the host's stdout).</param>
    /// <param name="json">The message's JSON text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NativeMessagingFrameException">The UTF-8 encoding of <paramref name="json"/> exceeds <see cref="MaxOutgoingBytes"/>.</exception>
    public static async Task WriteAsync(Stream output, string json, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxOutgoingBytes)
        {
            throw new NativeMessagingFrameException($"The reply is too large ({body.Length} bytes; the limit is {MaxOutgoingBytes}).");
        }

        var frame = new byte[4 + body.Length];
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        }

        body.CopyTo(frame, 4);

        // One write so the prefix and body reach the pipe together.
        await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadFullyAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await input.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
