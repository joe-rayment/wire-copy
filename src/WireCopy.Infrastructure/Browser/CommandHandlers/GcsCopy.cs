// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Width-aware copy helpers for the GCS setup flow (workspace-ur5h).
///
/// <para>
/// The previous implementation (workspace-cgnt) shipped strings ~95-100 chars
/// long that visibly truncated on a 100-col terminal. The bead's hard rule:
/// "Wrap or shorten — never truncate." This helper centralises the rule so
/// every visible string in the GCS flow is bounded by
/// <c>min(terminalWidth, 80) - indent - boxBorders</c>.
/// </para>
/// </summary>
internal static class GcsCopy
{
    /// <summary>
    /// Hard upper bound on the effective copy width. Mirrors the design
    /// system convention that single-column setup screens cap at 80 cols.
    /// </summary>
    public const int MaxAbsoluteWidth = 80;

    /// <summary>
    /// Returns the effective copy width for a line of text rendered next to
    /// a FormField sized to <paramref name="fieldWidth"/>. Subtracts a small
    /// margin for the leading indent and trailing slack so subtitles, help
    /// text, and overlay copy fit comfortably without wrapping mid-word.
    /// </summary>
    /// <param name="fieldWidth">Width of the surrounding FormField box, including borders.</param>
    /// <returns>Maximum string length usable for visible copy.</returns>
    public static int MaxCopyWidth(int fieldWidth)
    {
        // 6 = 2 left margin (indent) + 4 box border / breathing room.
        var effective = Math.Min(MaxAbsoluteWidth, fieldWidth) - 6;
        return Math.Max(20, effective);
    }

    /// <summary>
    /// Returns <paramref name="primary"/> when it fits within
    /// <paramref name="maxLen"/>, else <paramref name="fallback"/> when it
    /// fits, else a hard-cropped <paramref name="fallback"/>. This is the
    /// ONLY place in the GCS setup flow where copy may be cropped — call
    /// sites surface a curated fallback so the user sees a complete sentence
    /// at narrow widths instead of a mid-word truncation.
    /// </summary>
    public static string FitOrShorten(string primary, string fallback, int maxLen)
    {
        if (string.IsNullOrEmpty(primary))
        {
            return primary;
        }

        if (primary.Length <= maxLen)
        {
            return primary;
        }

        if (!string.IsNullOrEmpty(fallback) && fallback.Length <= maxLen)
        {
            return fallback;
        }

        // Last resort: hard-crop the fallback (or the primary if no
        // fallback). At ridiculously narrow widths we still need to render
        // something — but FitOrShorten is the only place this happens, so
        // tests can assert "FitOrShorten was the ceiling" without false
        // positives elsewhere.
        var src = string.IsNullOrEmpty(fallback) ? primary : fallback;
        return src.Length <= maxLen ? src : src[..Math.Max(1, maxLen)];
    }

    /// <summary>
    /// Word-wraps a string into lines that each fit within <paramref name="maxLen"/>.
    /// Used by the help overlay so multi-sentence copy reads correctly at
    /// narrow widths instead of running off the right edge.
    /// </summary>
    public static IReadOnlyList<string> WrapToWidth(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        if (maxLen <= 0)
        {
            return new[] { text };
        }

        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();

        foreach (var word in words)
        {
            // Word is too long to ever fit — push current, then split the
            // word itself.
            if (word.Length > maxLen)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                var w = word;
                while (w.Length > maxLen)
                {
                    lines.Add(w[..maxLen]);
                    w = w[maxLen..];
                }

                if (w.Length > 0)
                {
                    current.Append(w);
                }

                continue;
            }

            var prospective = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
            if (prospective > maxLen)
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
            else
            {
                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }
}
