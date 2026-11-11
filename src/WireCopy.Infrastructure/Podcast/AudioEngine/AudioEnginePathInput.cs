// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>Turns whatever a person pastes for the audio-engine location into an absolute path.</summary>
internal static partial class AudioEnginePathInput
{
    /// <summary>Normalizes pasted text: quotes, <c>file://</c>, <c>~</c>, trailing slash and drag-and-drop escapes.</summary>
    /// <param name="raw">The text as typed or pasted.</param>
    /// <param name="home">The user's home folder.</param>
    /// <param name="exists">Whether a path exists as a file or folder.</param>
    /// <returns>An absolute path; empty when <paramref name="raw"/> is blank.</returns>
    internal static string Normalize(string raw, string home, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var text = (raw ?? string.Empty).Trim();
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
        {
            text = text[1..^1].Trim();
        }

        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.IsFile)
        {
            text = uri.LocalPath;
        }

        text = TrimTrailingSlash(text);
        var path = AudioEngineLauncher.ExpandHome(text, home);
        path = TrimTrailingSlash(path);
        if (!OperatingSystem.IsWindows() && !exists(path))
        {
            var unescaped = EscapedChar().Replace(text, "$1");
            if (!string.Equals(unescaped, text, StringComparison.Ordinal))
            {
                var candidate = TrimTrailingSlash(AudioEngineLauncher.ExpandHome(unescaped, home));
                if (exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return path;
    }

    /// <summary>Drops trailing slashes, keeping a bare root.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The path without trailing separators.</returns>
    private static string TrimTrailingSlash(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? path[..Math.Min(1, path.Length)] : trimmed;
    }

    /// <summary>A backslash followed by a non-alphanumeric character (Terminal drag-and-drop escape).</summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"\\([^A-Za-z0-9])")]
    private static partial Regex EscapedChar();
}
