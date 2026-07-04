// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-v3pz: copies text to the SYSTEM clipboard from inside the TUI using
/// the OSC 52 terminal escape (<c>ESC ] 52 ; c ; &lt;base64&gt; BEL</c>). The
/// terminal emulator — not the app — performs the copy, so it works over SSH and
/// needs no <c>pbcopy</c>/<c>xclip</c> dependency. Caveats surfaced to the user
/// elsewhere: the terminal must have OSC 52 enabled, and under tmux it needs
/// <c>set -g set-clipboard on</c>; the file-export path is the guaranteed
/// fallback. Terminals cap the payload, so we refuse oversized copies rather
/// than emit a truncated clipboard.
/// </summary>
internal static class Osc52Clipboard
{
    /// <summary>
    /// Conservative cap on the base64 payload (bytes). Most terminals accept
    /// ~100 KB of OSC 52; we stay well under and let export handle the rest.
    /// </summary>
    internal const int MaxBase64Length = 74994;

    /// <summary>Builds the OSC 52 escape sequence for <paramref name="text"/>, or null if it is too large.</summary>
    internal static string? BuildSequence(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (b64.Length > MaxBase64Length)
        {
            return null;
        }

        return $"\x1b]52;c;{b64}\x07";
    }

    /// <summary>
    /// Writes the OSC 52 copy sequence to stdout. Returns false when the text is
    /// too large for a single sequence (caller should fall back to export).
    /// </summary>
    internal static bool Copy(string text, TextWriter? writer = null)
    {
        var seq = BuildSequence(text);
        if (seq is null)
        {
            return false;
        }

        (writer ?? Console.Out).Write(seq);
        (writer ?? Console.Out).Flush();
        return true;
    }
}
