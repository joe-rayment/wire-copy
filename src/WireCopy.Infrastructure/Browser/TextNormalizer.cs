// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Normalizes link/title text pulled out of HTML or RSS so downstream
/// renderers (link tree, AI Curated, podcast titles, status bar) work with
/// already-decoded plain text. Decodes named/numeric/hex HTML entities,
/// converts U+00A0 (non-breaking space from <c>&amp;nbsp;</c>) to a regular
/// space — terminal fonts vary in how they render U+00A0 — then collapses
/// whitespace runs and trims.
/// </summary>
internal static partial class TextNormalizer
{
    /// <summary>
    /// Returns null/empty input unchanged. Otherwise decodes HTML entities
    /// (named like <c>&amp;nbsp;</c>, numeric like <c>&amp;#39;</c>, and hex
    /// like <c>&amp;#x27;</c>), normalizes U+00A0 to a regular space,
    /// collapses internal whitespace runs to a single space, and trims.
    /// </summary>
    public static string NormalizeDisplayText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var decoded = HtmlEntity.DeEntitize(text) ?? text;
        decoded = decoded.Replace(' ', ' ');
        decoded = WhitespaceRun().Replace(decoded, " ");
        return decoded.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
