// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-t1ok.3: the user-label ledger's state helpers. Labels are the
/// durable record of the user's hand corrections; these helpers keep that
/// state consistent across label sessions (<see cref="MergeLabels"/>) and
/// across model rounds (<see cref="CarryUserState"/> — every analyzer parse
/// builds a brand-new <see cref="SiteHierarchyConfig"/>, which would silently
/// drop the ledger without an explicit carry).
/// </summary>
internal static class LabelDerivation
{
    /// <summary>Strips scheme, leading www., query, fragment and trailing slash; lower-cases.</summary>
    internal static string NormalizeUrl(string url)
    {
        var s = url.Trim();
        if (s.Length == 0)
        {
            return s;
        }

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }

        var hash = s.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            s = s[..hash];
        }

        var query = s.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            s = s[..query];
        }

        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            s = s[4..];
        }

        return s.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// Merges a label session's outcome into the saved ledger. Latest wins per
    /// normalized URL. A prior label whose URL the session SAW but no longer
    /// labels was cleared by the user — dropped; a prior label whose URL was
    /// not on the page is kept (the link may rotate back). Article ranks are
    /// re-compacted to 1..N: this session's order first, then surviving prior
    /// articles in their saved order. Capped at
    /// <see cref="SiteHierarchyConfig.MaxUserLabels"/> (articles survive first,
    /// then most recent).
    /// </summary>
    internal static List<UserLinkLabel> MergeLabels(
        IReadOnlyList<UserLinkLabel> prior,
        IReadOnlyList<UserLinkLabel> latest,
        IReadOnlyCollection<string>? seenUrls = null)
    {
        var latestByKey = new Dictionary<string, UserLinkLabel>(StringComparer.Ordinal);
        foreach (var label in latest)
        {
            latestByKey[NormalizeUrl(label.Url)] = label;
        }

        var seen = seenUrls == null
            ? null
            : new HashSet<string>(seenUrls.Select(NormalizeUrl), StringComparer.Ordinal);

        var keptPrior = prior
            .Where(p =>
            {
                var key = NormalizeUrl(p.Url);
                if (latestByKey.ContainsKey(key))
                {
                    return false; // overridden this session
                }

                // Seen on the page but not in the outcome => the user cleared it.
                return seen == null || !seen.Contains(key);
            })
            .ToList();

        var merged = latest.Concat(keptPrior).ToList();
        if (merged.Count > SiteHierarchyConfig.MaxUserLabels)
        {
            merged = merged
                .OrderByDescending(l => l.Kind == LinkLabelKind.Article)
                .ThenByDescending(l => l.LabeledAt)
                .Take(SiteHierarchyConfig.MaxUserLabels)
                .ToList();
        }

        // Re-compact article ranks by URL key: session order first, then the
        // surviving prior articles in their saved order.
        var rankByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var article in latest.Where(l => l.Kind == LinkLabelKind.Article).OrderBy(l => l.Rank ?? int.MaxValue)
                     .Concat(keptPrior.Where(l => l.Kind == LinkLabelKind.Article).OrderBy(l => l.Rank ?? int.MaxValue)))
        {
            rankByKey.TryAdd(NormalizeUrl(article.Url), rankByKey.Count + 1);
        }

        return merged
            .Select(l => l.Kind == LinkLabelKind.Article && rankByKey.TryGetValue(NormalizeUrl(l.Url), out var r)
                ? l with { Rank = r }
                : l)
            .ToList();
    }

    /// <summary>
    /// Carries the user's durable state onto a config freshly parsed from a
    /// model response. <see cref="OpenAiHierarchyAnalyzer"/>'s parser constructs
    /// a brand-new <see cref="SiteHierarchyConfig"/> on EVERY round (initial
    /// infer, degenerate/ordering repair, confirm re-infer, adjust refine), so
    /// the ledger, instruction log and More-menu rules — none of which the
    /// model can express — must be copied forward or a single refine would
    /// silently erase every prior hand correction.
    /// </summary>
    internal static SiteHierarchyConfig CarryUserState(SiteHierarchyConfig fresh, SiteHierarchyConfig? prior)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (prior == null)
        {
            return fresh;
        }

        return fresh with
        {
            UserLabels = fresh.UserLabels.Count > 0 ? fresh.UserLabels : prior.UserLabels,
            UserInstructions = fresh.UserInstructions.Count > 0 ? fresh.UserInstructions : prior.UserInstructions,
            MoreSelectors = fresh.MoreSelectors.Count > 0 ? fresh.MoreSelectors : prior.MoreSelectors,
            MoreUrlPatterns = fresh.MoreUrlPatterns.Count > 0 ? fresh.MoreUrlPatterns : prior.MoreUrlPatterns,
        };
    }
}
