// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI;

/// <summary>
/// Provides a shared lock for thread-safe console output.
/// All Console.Write/Console.SetCursorPosition calls from renderers
/// and animation ticks must acquire this lock to prevent interleaved output.
/// </summary>
public static class ConsoleSync
{
    /// <summary>
    /// Shared lock object for serializing console output operations.
    /// Acquire this lock before any Console.Write or Console.SetCursorPosition call
    /// that could race with animation timer ticks or background render updates.
    /// </summary>
    public static readonly object Lock = new();
}
