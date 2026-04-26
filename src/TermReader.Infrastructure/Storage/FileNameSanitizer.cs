// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Infrastructure.Storage;

/// <summary>
/// Sanitizes file names by replacing invalid characters.
/// </summary>
internal static class FileNameSanitizer
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Sanitize(string name)
    {
        return string.Concat(name.Select(c => InvalidChars.Contains(c) ? '_' : c));
    }
}
