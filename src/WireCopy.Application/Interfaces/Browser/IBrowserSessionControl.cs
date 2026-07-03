// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Application-layer interface for browser session lifecycle management.
/// Provides warmup and disposal without exposing browser automation types.
/// </summary>
public interface IBrowserSessionControl : IDisposable
{
    /// <summary>
    /// Eagerly initializes the browser driver in the background so the first
    /// browser-based page load does not incur the cold-start penalty.
    /// </summary>
    Task WarmUpAsync();

    /// <summary>
    /// workspace-9k27.7: captures the identity (bundle id) of the terminal app
    /// hosting the TUI so focus can be returned to it after browser windows
    /// appear. MUST be called at process startup, while the terminal is still
    /// the frontmost app — capturing lazily at first browser launch recorded
    /// whatever app the user had switched to in the meantime. macOS-only; no-op
    /// elsewhere and on failure (refocus then falls back to a TERM_PROGRAM map).
    /// </summary>
    Task CaptureTerminalIdentityAsync();
}
