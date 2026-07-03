// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Pure helpers for returning macOS keyboard focus to the terminal that launched the app
/// (workspace-75ng). Kept separate from <see cref="BrowserSession"/> — the way
/// <see cref="DockGeometry"/> is — so the osascript command construction is unit-testable
/// cross-platform without spawning a real process or requiring a Mac.
///
/// <para>
/// The bug this fixes: the old code mapped <c>TERM_PROGRAM</c> by name and fell back to
/// Apple <c>Terminal</c> for anything unrecognised, so Ghostty (<c>TERM_PROGRAM=ghostty</c>)
/// returned focus to the WRONG app and the keyboard stopped driving the TUI. The robust
/// path now CAPTURES the frontmost GUI app's bundle id ONCE at startup — when the terminal
/// still owns focus — and re-activates THAT specific app on every refocus, terminal-agnostic.
/// The <c>TERM_PROGRAM</c> name map survives only as a cheap fallback for when the capture
/// failed (e.g. Automation permission not yet granted), and it no longer guesses
/// <c>Terminal</c> for unknown terminals (returning null instead so we never focus-steal to
/// the wrong app).
/// </para>
/// </summary>
internal static class TerminalRefocus
{
    /// <summary>
    /// osascript expression that prints the bundle identifier of the frontmost GUI app.
    /// Run ONCE at startup (before the browser can take focus) so the captured id is the
    /// terminal's. Requires Automation permission for System Events the first time.
    /// </summary>
    internal const string CaptureFrontmostBundleIdScript =
        "tell application \"System Events\" to get bundle identifier of " +
        "(first application process whose frontmost is true)";

    /// <summary>
    /// Builds the osascript expression that returns keyboard focus to the captured terminal.
    /// Prefers the bundle id (terminal-agnostic, survives an app rename); falls back to the
    /// <paramref name="appName"/> derived from <c>TERM_PROGRAM</c>. Returns null when neither
    /// is known — better no refocus than refocusing the WRONG app (the Ghostty bug).
    /// </summary>
    internal static string? BuildActivateScript(string? bundleId, string? appName)
    {
        if (!string.IsNullOrWhiteSpace(bundleId))
        {
            // `application id "..."` targets the exact app we captured regardless of its
            // display name, so it works for ANY terminal including ones we never name-mapped.
            return $"tell application id \"{EscapeForAppleScript(bundleId)}\" to activate";
        }

        if (!string.IsNullOrWhiteSpace(appName))
        {
            return $"tell application \"{EscapeForAppleScript(appName)}\" to activate";
        }

        return null;
    }

    /// <summary>
    /// Resolves the refocus script from the captured bundle id and the <c>TERM_PROGRAM</c>
    /// environment value. Bundle id wins; the name map is the fallback. Pure — the whole
    /// decision is exercised by unit tests without touching a process.
    /// </summary>
    internal static string? ResolveActivateScript(string? capturedBundleId, string? termProgram) =>
        BuildActivateScript(capturedBundleId, MapTermProgramToAppName(termProgram));

    /// <summary>
    /// Maps <c>TERM_PROGRAM</c> to the macOS application name as a FALLBACK for when the
    /// frontmost-bundle-id capture failed. Ghostty and Warp are handled explicitly so they
    /// no longer fall through to Apple Terminal (the workspace-75ng bug). Unknown/empty
    /// values return null — the caller must NOT guess <c>Terminal</c> and steal focus to the
    /// wrong app.
    /// </summary>
    internal static string? MapTermProgramToAppName(string? termProgram)
    {
        if (string.IsNullOrWhiteSpace(termProgram))
        {
            return null;
        }

        return termProgram switch
        {
            "iTerm.app" => "iTerm2",
            "Apple_Terminal" => "Terminal",
            "WezTerm" => "WezTerm",
            "Alacritty" => "Alacritty",
            "kitty" => "kitty",
            "ghostty" or "Ghostty" => "Ghostty",
            "WarpTerminal" or "Warp" => "Warp",
            _ => null,
        };
    }

    /// <summary>
    /// True when <paramref name="bundleId"/> looks like a usable captured value — a non-empty
    /// reverse-DNS-ish identifier. osascript prints an empty line (or an error) when the
    /// capture fails or Automation permission is denied; those must NOT be cached as the
    /// terminal's id, or every later refocus would target nothing.
    /// </summary>
    internal static bool IsUsableBundleId(string? bundleId)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
        {
            return false;
        }

        var trimmed = bundleId.Trim();

        // A real bundle id contains a dot (com.mitchellh.ghostty, com.apple.Terminal …) and
        // no whitespace. An osascript error or a "missing value" reply fails one of these.
        return trimmed.Contains('.', StringComparison.Ordinal)
            && !trimmed.Any(char.IsWhiteSpace);
    }

    /// <summary>
    /// Atomically claims the refocus debounce slot (workspace-9k27.7). A forced claim
    /// always wins and stamps the timestamp. A non-forced claim only proceeds when the
    /// window has elapsed AND it wins the CompareExchange — crucially, a SKIPPED call
    /// never writes the timestamp, so a burst of skipped calls cannot keep extending
    /// the window and starve non-forced refocus forever (the old Exchange-before-check
    /// bug). Pure state-machine over a caller-owned ticks field: unit-testable.
    /// </summary>
    /// <param name="lastClaimTicks">Caller-owned field holding the last successful claim's ticks.</param>
    /// <param name="nowTicks">Current time in ticks.</param>
    /// <param name="force">True to bypass the debounce window entirely.</param>
    /// <param name="windowTicks">Debounce window length in ticks.</param>
    /// <returns>True when the caller should perform the refocus.</returns>
    internal static bool TryClaimRefocusSlot(ref long lastClaimTicks, long nowTicks, bool force, long windowTicks)
    {
        if (force)
        {
            Interlocked.Exchange(ref lastClaimTicks, nowTicks);
            return true;
        }

        var previous = Interlocked.Read(ref lastClaimTicks);
        if (nowTicks - previous < windowTicks)
        {
            return false; // skipped — deliberately does NOT touch the timestamp
        }

        // CompareExchange so two racing callers can't both claim the same slot: the
        // loser sees a changed value and skips (its rival is already refocusing).
        return Interlocked.CompareExchange(ref lastClaimTicks, nowTicks, previous) == previous;
    }

    private static string EscapeForAppleScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);
}
