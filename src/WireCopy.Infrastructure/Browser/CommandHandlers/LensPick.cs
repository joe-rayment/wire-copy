// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-6yb7.5: one click captured by <see cref="PickScript"/> on the
/// sidecar lens — the element the user pointed at, expressed in the same
/// vocabulary the link pipeline uses (href + display text + the
/// LinkExtractor-format parent chain).
/// </summary>
internal sealed record LensPick(string Href, string Text, string Parent)
{
    /// <summary>Parses <see cref="PickScript.Poll"/>'s JSON payload; null for empty/garbled output.</summary>
    internal static LensPick? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var href = root.TryGetProperty("href", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var parent = root.TryGetProperty("parent", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            return new LensPick(href!, text, parent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the pick onto the page's extracted links. An URL match returns the
    /// REAL <see cref="LinkInfo"/> (whose ParentSelector came from the same DOM
    /// via HtmlAgilityPack — the best durable-identifier source); otherwise a
    /// content-typed stand-in is constructed from the click payload so the pick
    /// still works on links the extractor skipped.
    /// </summary>
    internal LinkInfo ToLinkInfo(IReadOnlyList<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var match = links.FirstOrDefault(l => !l.IsGroupHeader && UrlsEqual(l.Url, Href));
        if (match != null)
        {
            return match;
        }

        return new LinkInfo
        {
            Url = Href,
            DisplayText = Text.Length > 0 ? Text : Href,
            Type = LinkType.Content,
            ImportanceScore = 70,
            ParentSelector = string.IsNullOrWhiteSpace(Parent) ? null : Parent,
        };
    }

    /// <summary>
    /// URL equality via the canonical <see cref="Cache.UrlNormalizer"/> (the
    /// same comparison DockSpotlight uses) — handles host casing, trailing
    /// slashes, fragments, and tracking params, so a clicked
    /// <c>…?utm_source=…#frag</c> still matches its extracted link.
    /// </summary>
    internal static bool UrlsEqual(string a, string b) =>
        string.Equals(
            Cache.UrlNormalizer.Normalize(a),
            Cache.UrlNormalizer.Normalize(b),
            StringComparison.OrdinalIgnoreCase);
}
