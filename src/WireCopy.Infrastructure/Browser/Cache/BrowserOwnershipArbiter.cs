// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.Cache;

/// <summary>
/// Pure decision logic for sharing the ONE real browser between the app and the
/// user (workspace-mya7). The user always wins: recent input anywhere in the
/// shared browser means prefetch must pause; resumption requires a longer quiet
/// period so the app never fights someone who is merely pausing to read.
///
/// <para>
/// Deliberately input-driven only — window focus is NOT a signal. On hosts
/// without a window manager (Xvfb/CI) and after our own dock/refocus dance,
/// focus state is unreliable; a human grabbing the browser always produces
/// input (the click that focuses it) within milliseconds.
/// </para>
/// </summary>
internal static class BrowserOwnershipArbiter
{
    /// <summary>
    /// True when browser-side input happened within <paramref name="inputWindow"/> —
    /// the user is actively using the browser and prefetch must pause NOW.
    /// </summary>
    public static bool IsUserActive(DateTimeOffset? lastBrowserInput, DateTimeOffset now, TimeSpan inputWindow)
        => lastBrowserInput.HasValue && now - lastBrowserInput.Value < inputWindow;

    /// <summary>
    /// True when the browser has been quiet for at least <paramref name="resumeIdle"/> —
    /// a paused prefetch may continue from its checkpoint.
    /// </summary>
    public static bool CanResume(DateTimeOffset? lastBrowserInput, DateTimeOffset now, TimeSpan resumeIdle)
        => !lastBrowserInput.HasValue || now - lastBrowserInput.Value >= resumeIdle;
}
