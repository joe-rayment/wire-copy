// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// A native-messaging frame could not be read or written (oversize or truncated).
/// The host replies with an error where it still can and exits.
/// </summary>
public sealed class NativeMessagingFrameException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NativeMessagingFrameException"/> class.</summary>
    /// <param name="message">One human sentence describing what was wrong with the frame.</param>
    public NativeMessagingFrameException(string message)
        : base(message)
    {
    }
}
