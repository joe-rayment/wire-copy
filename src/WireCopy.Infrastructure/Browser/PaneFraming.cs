// Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Wire framing shared by both halves of the browser-hosted web pane IPC. Every message is
/// <c>[4-byte big-endian payload length][1 type byte][payload]</c>, where the length covers the type
/// byte plus the payload. Centralized (and made internal-testable) so the encode/decode logic has one
/// home and a round-trip unit test, rather than being duplicated and drifting between the child and
/// host sides.
/// </summary>
internal static class PaneFraming
{
    /// <summary>Child→host: a JPEG screencast frame.</summary>
    public const byte TypeFrame = 1;

    /// <summary>Host→child: a UTF-8 JSON pointer/keyboard/navigation input message.</summary>
    public const byte TypeInput = 2;

    /// <summary>Child→host: a UTF-8 JSON control message (pane mode / visibility).</summary>
    public const byte TypeControl = 3;

    /// <summary>Largest payload accepted by a reader; guards against a desync reading garbage lengths.</summary>
    public const int MaxPayload = 16 * 1024 * 1024;

    /// <summary>Builds a framed message for <paramref name="type"/> wrapping <paramref name="payload"/>.</summary>
    public static byte[] Encode(byte type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = type;
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    /// <summary>
    /// Parses a single complete frame (as produced by <see cref="Encode"/>). Returns false when the
    /// buffer is too short or the declared length is invalid. Used by tests; the live read loops do
    /// streaming reads via <c>ReadExactAsync</c> using the same length/type layout.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out byte type, out byte[] payload)
    {
        type = 0;
        payload = Array.Empty<byte>();
        if (frame.Length < 5)
        {
            return false;
        }

        var len = BinaryPrimitives.ReadInt32BigEndian(frame);
        if (len <= 0 || len > MaxPayload || frame.Length < 4 + len)
        {
            return false;
        }

        type = frame[4];
        payload = frame.Slice(5, len - 1).ToArray();
        return true;
    }
}
