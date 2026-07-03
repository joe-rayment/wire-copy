// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Pure helpers for the side-by-side sidecar tiling (workspace-75ng.4): read the terminal
/// window's bounds, resize it to the side NOT taken by the docked browser, and restore it on
/// dismiss. Kept separate from <see cref="BrowserSession"/> — like <see cref="DockGeometry"/>
/// and <see cref="TerminalRefocus"/> — so the osascript construction and the complement
/// geometry are unit-testable cross-platform without a Mac.
///
/// <para>
/// The actual window move requires ACCESSIBILITY permission for the terminal app (System
/// Settings → Privacy &amp; Security → Accessibility) — distinct from the Automation grant
/// used to return focus. System Events <c>position</c>/<c>size</c> of a process window use
/// top-left-origin screen pixels, the same coordinate space as CDP <c>setWindowBounds</c> and
/// <c>screen.avail*</c>, so the browser dock and the terminal tile compose.
/// </para>
/// </summary>
internal static class TerminalTiling
{
    /// <summary>Stdout of <see cref="BuildRestoreMatchedWindowScript"/> when a window was restored.</summary>
    internal const string RestoreResultRestored = "restored";

    /// <summary>Stdout of <see cref="BuildRestoreMatchedWindowScript"/> when no window matched the tile.</summary>
    internal const string RestoreResultNoMatch = "no-match";

    /// <summary>
    /// osascript that prints the front window's position and size for the process with the
    /// given bundle id, as "x, y, w, h". Used to capture the terminal's bounds before tiling
    /// so dismiss can restore them.
    /// </summary>
    internal static string BuildGetBoundsScript(string bundleId) =>
        "tell application \"System Events\" to tell (first process whose bundle identifier is \""
        + EscapeForAppleScript(bundleId)
        + "\") to get (get position of window 1) & (get size of window 1)";

    /// <summary>
    /// osascript that moves and resizes the front window of the process with the given bundle
    /// id to <paramref name="rect"/>. Set position before size so the window lands on the
    /// intended display before it grows.
    /// </summary>
    internal static string BuildSetBoundsScript(string bundleId, WindowRect rect)
    {
        var id = EscapeForAppleScript(bundleId);
        var x = rect.X.ToString(CultureInfo.InvariantCulture);
        var y = rect.Y.ToString(CultureInfo.InvariantCulture);
        var w = rect.Width.ToString(CultureInfo.InvariantCulture);
        var h = rect.Height.ToString(CultureInfo.InvariantCulture);
        return "tell application \"System Events\" to tell (first process whose bundle identifier is \""
            + id + "\")\n"
            + "set position of window 1 to {" + x + ", " + y + "}\n"
            + "set size of window 1 to {" + w + ", " + h + "}\n"
            + "end tell";
    }

    /// <summary>
    /// osascript that finds the terminal window still sitting ON the tile we set (within
    /// <paramref name="tolerancePx"/>) and restores THAT window to <paramref name="restoreTo"/>,
    /// printing <see cref="RestoreResultRestored"/> or <see cref="RestoreResultNoMatch"/>.
    ///
    /// <para>
    /// workspace-9k27.6 (minor): the plain set-bounds script acts on <c>window 1</c> — the
    /// terminal app's FRONTMOST window at restore time, which in a multi-window terminal may
    /// not be the window we tiled. Matching by the tiled bounds targets the right window and
    /// doubles as the "user rearranged it" check: a moved/resized window matches nothing, so
    /// the user's layout is never clobbered.
    /// </para>
    /// </summary>
    internal static string BuildRestoreMatchedWindowScript(
        string bundleId, WindowRect expectedTile, WindowRect restoreTo, int tolerancePx)
    {
        var id = EscapeForAppleScript(bundleId);
        var inv = CultureInfo.InvariantCulture;
        var tol = Math.Max(0, tolerancePx).ToString(inv);
        return
            "tell application \"System Events\" to tell (first process whose bundle identifier is \"" + id + "\")\n"
            + "repeat with w in windows\n"
            + "set {wx, wy} to position of w\n"
            + "set {ww, wh} to size of w\n"
            + "set dx to wx - (" + expectedTile.X.ToString(inv) + ")\n"
            + "if dx < 0 then set dx to -dx\n"
            + "set dy to wy - (" + expectedTile.Y.ToString(inv) + ")\n"
            + "if dy < 0 then set dy to -dy\n"
            + "set dw to ww - (" + expectedTile.Width.ToString(inv) + ")\n"
            + "if dw < 0 then set dw to -dw\n"
            + "set dh to wh - (" + expectedTile.Height.ToString(inv) + ")\n"
            + "if dh < 0 then set dh to -dh\n"
            + "if dx <= " + tol + " and dy <= " + tol + " and dw <= " + tol + " and dh <= " + tol + " then\n"
            + "set position of w to {" + restoreTo.X.ToString(inv) + ", " + restoreTo.Y.ToString(inv) + "}\n"
            + "set size of w to {" + restoreTo.Width.ToString(inv) + ", " + restoreTo.Height.ToString(inv) + "}\n"
            + "return \"" + RestoreResultRestored + "\"\n"
            + "end if\n"
            + "end repeat\n"
            + "return \"" + RestoreResultNoMatch + "\"\n"
            + "end tell";
    }

    /// <summary>
    /// Parses the "x, y, w, h" reply from <see cref="BuildGetBoundsScript"/> (osascript joins
    /// the two AppleScript lists with ", "). Returns null on any malformed/short output so a
    /// failed capture never resizes the terminal to garbage.
    /// </summary>
    internal static WindowRect? TryParseBounds(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var parts = output.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        var values = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        // A zero-or-negative size is not a real window — reject it.
        if (values[2] <= 0 || values[3] <= 0)
        {
            return null;
        }

        return new WindowRect(values[0], values[1], values[2], values[3]);
    }

    /// <summary>
    /// Computes the terminal's tile: the full-height slice of the work area NOT occupied by
    /// the docked browser. When the browser is on the right, the terminal takes the left
    /// portion (and vice-versa). <paramref name="browserWidth"/> is the docked browser's
    /// width; the terminal gets the remaining width. Returns null if there is no usable space
    /// left for the terminal (browser as wide as the screen).
    /// </summary>
    internal static WindowRect? ComputeTerminalRect(
        int availLeft, int availTop, int availWidth, int availHeight, int browserWidth, DockSide browserSide)
    {
        if (availWidth <= 0 || availHeight <= 0)
        {
            return null;
        }

        var clampedBrowser = Math.Clamp(browserWidth, 0, availWidth);
        var terminalWidth = availWidth - clampedBrowser;
        if (terminalWidth <= 0)
        {
            return null;
        }

        // Browser on the right → terminal hugs the left edge; browser on the left → terminal
        // sits to the right of it.
        var terminalX = browserSide == DockSide.Right
            ? availLeft
            : availLeft + clampedBrowser;

        return new WindowRect(terminalX, availTop, terminalWidth, availHeight);
    }

    private static string EscapeForAppleScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>A window rectangle in top-left-origin screen pixels.</summary>
    internal readonly record struct WindowRect(int X, int Y, int Width, int Height);
}
