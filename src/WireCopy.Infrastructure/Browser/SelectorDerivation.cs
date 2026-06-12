// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-5oe9.3 — deterministic derivation of durable, generalizing
/// identifiers (CSS parent-selector fragments and URL path patterns) shared by
/// a bucket of links.
///
/// <para><b>Precedence contract:</b> this is a FALLBACK. The OpenAI hierarchy
/// analyzer already returns <c>parentSelectors</c>/<c>urlPatterns</c> per
/// section, so callers (e.g. <c>AiCuratedStrategy</c>, workspace-5oe9.5) MUST
/// prefer valid model-returned selectors and invoke this helper ONLY for
/// buckets where the model omitted them. A weak derivation here degrades
/// gracefully (the bucket simply matches nothing extra) rather than corrupting
/// the durable config.</para>
///
/// <para>Both methods are pure and deterministic. They discard
/// non-discriminating signals — bare tag fragments like <c>a</c> /
/// <c>article &gt; a</c> and purely-numeric URL segments (dates) — so the
/// derived identifier generalizes to a later visit instead of pinning to one
/// snapshot.</para>
/// </summary>
internal static class SelectorDerivation
{
    // Combinators / whitespace that separate CSS compound selectors.
    private static readonly Regex SelectorSplit = new(@"[\s>+~]+", RegexOptions.Compiled);

    // workspace-romy.10: id fragments containing 2+ digits are volatile
    // per-item / per-day stamps (techmeme '#0i1', memeorandum '#260611p108'),
    // never durable structure ('#hiring', '#topcol2' survive).
    private static readonly Regex VolatileIdFragment = new(
        @"#(?=[A-Za-z0-9_-]*\d[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]+",
        RegexOptions.Compiled);

    /// <summary>
    /// workspace-romy.10: strips volatile (digit-bearing) id fragments from a
    /// selector so model-returned identifiers stay durable across visits. On
    /// memeorandum the analyzer kept returning 'div.item#260611p108'-style
    /// selectors — valid today, matching ONE item even today, nothing
    /// tomorrow — which collapsed coverage below the degenerate gate.
    /// Removing the id only ever broadens a selector, so the sanitized
    /// fragment matches at least everything the original did.
    /// </summary>
    public static string StripVolatileIds(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return selector;
        }

        var stripped = VolatileIdFragment.Replace(selector, string.Empty);

        // Drop compounds left empty (a bare '#260611p44' between combinators)
        // so we don't emit 'div.clus >  > div.ii'.
        var parts = stripped
            .Split(['>', '+', '~'], StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0);
        return string.Join(" > ", parts);
    }

    /// <summary>
    /// Derives the shared, discriminating parent-selector fragment(s) common to
    /// every link's <see cref="LinkInfo.ParentSelector"/>. A fragment is
    /// "discriminating" only if it carries a class (<c>.</c>), id (<c>#</c>), or
    /// attribute (<c>[</c>) — bare element names (<c>a</c>, <c>div</c>,
    /// <c>section</c>, …) are dropped because they match almost everything.
    /// Returns an empty list when there is no shared discriminating signal.
    /// </summary>
    public static List<string> DeriveParentSelectors(IEnumerable<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var linkList = links.ToList();

        // A bucket member with no selector means we cannot guarantee a shared
        // fragment — bail to empty rather than over-generalize.
        if (linkList.Count == 0 || linkList.Any(l => string.IsNullOrWhiteSpace(l.ParentSelector)))
        {
            return new List<string>();
        }

        var tokenSets = linkList
            .Select(l => SelectorSplit.Split(l.ParentSelector!.Trim())
                .Where(t => t.Length > 0)
                .Select(t => t.Trim())
                .ToHashSet(StringComparer.Ordinal))
            .ToList();

        // Intersect across all links, keep only discriminating tokens.
        IEnumerable<string> common = tokenSets[0];
        foreach (var set in tokenSets.Skip(1))
        {
            common = common.Intersect(set, StringComparer.Ordinal);
        }

        // Shortest-discriminating-first, then ordinal — deterministic.
        return common
            .Where(IsDiscriminating)
            .OrderBy(t => t.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Derives the shared URL path pattern(s) common to every link — e.g. links
    /// under <c>/opinion/2026/05/30/...</c> yield <c>/opinion/</c>. Purely
    /// numeric segments (dates, ids) are discarded so the pattern does not pin
    /// to a single day's URLs. Patterns are returned in path order (most
    /// significant first). Empty when no shared non-numeric segment exists.
    /// </summary>
    public static List<string> DeriveUrlPatterns(IEnumerable<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var segmentLists = new List<List<string>>();
        foreach (var link in links)
        {
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
            {
                return new List<string>();
            }

            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToLowerInvariant())
                .ToList();

            if (segments.Count == 0)
            {
                // A link at the site root has no path signal to share.
                return new List<string>();
            }

            segmentLists.Add(segments);
        }

        if (segmentLists.Count == 0)
        {
            return new List<string>();
        }

        // Segments common to ALL links, minus numeric-only (date/id) segments.
        IEnumerable<string> common = segmentLists[0].ToHashSet(StringComparer.Ordinal);
        foreach (var list in segmentLists.Skip(1))
        {
            common = common.Intersect(list.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        }

        var commonSet = common
            .Where(s => !IsNumeric(s))
            .ToHashSet(StringComparer.Ordinal);

        if (commonSet.Count == 0)
        {
            return new List<string>();
        }

        // Order by first appearance in the first link's path (most significant
        // first), de-duplicated; wrap as "/seg/" so Contains-matching does not
        // catch the segment inside an unrelated slug.
        return segmentLists[0]
            .Where(commonSet.Contains)
            .Distinct(StringComparer.Ordinal)
            .Select(seg => $"/{seg}/")
            .ToList();
    }

    /// <summary>
    /// The discriminating (class/id/attribute) CSS tokens in a single selector.
    /// Used by exclusion-rule derivation, which unions per-link signals rather
    /// than intersecting them. Empty for a null/blank or all-generic selector.
    /// </summary>
    public static IEnumerable<string> DiscriminatingTokens(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Enumerable.Empty<string>();
        }

        return SelectorSplit.Split(selector.Trim())
            .Where(t => t.Length > 0)
            .Where(IsDiscriminating);
    }

    /// <summary>
    /// The non-numeric path segments of a URL (lowercased), e.g.
    /// <c>/opinion/2026/05/a</c> → <c>opinion</c>, <c>a</c>. Numeric (date/id)
    /// segments are dropped. Empty for a non-absolute URL.
    /// </summary>
    public static IEnumerable<string> MeaningfulPathSegments(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Enumerable.Empty<string>();
        }

        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => !IsNumeric(s));
    }

    private static bool IsDiscriminating(string token) =>
        token.Contains('.', StringComparison.Ordinal)
        || token.Contains('#', StringComparison.Ordinal)
        || token.Contains('[', StringComparison.Ordinal);

    private static bool IsNumeric(string segment) =>
        segment.Length > 0 && segment.All(char.IsDigit);
}
