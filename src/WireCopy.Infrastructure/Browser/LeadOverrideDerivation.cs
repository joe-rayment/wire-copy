// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-zd96: derives a durable story-river selector from a single lead link
/// the user pointed at (by tree pick or by URL), WITHOUT asking the model.
///
/// <para>
/// The old lead-override path fed a text instruction to the model and let it
/// re-guess every section selector; on a messy aggregator (Techmeme) the model
/// returned a Top Story selector matching ZERO links and ad-shaped sections. The
/// DOM already tells us everything: the lead's parent selector plus the page's
/// link population. We pick the lead-selector token that best SEPARATES the
/// story headlines from the noise (source citations, discussion links, ads),
/// scoring each token by precision × recall over the actual links. On Techmeme
/// the lead selector <c>div.itc1 &gt; div.itc2 &gt; div.item &gt; div.ii &gt; strong.L3</c>
/// yields <c>div.ii</c> — 24 story headlines, 0 non-stories — over <c>div.item</c>
/// (matches every citation) or <c>strong.L3</c> (only one prominence tier).
/// </para>
/// </summary>
internal static class LeadOverrideDerivation
{
    /// <summary>Text length at/above which a link reads as a real story headline (mirrors LinkExtractor).</summary>
    internal const int MinStoryTextLength = 25;

    /// <summary>
    /// Returns a river derivation, or null when <paramref name="lead"/> has no
    /// usable selector or no discriminating token matches any story (the caller
    /// then falls back or fails honestly — never presents a 0-link section).
    /// </summary>
    internal static Result? Derive(LinkInfo lead, IReadOnlyList<LinkInfo> allLinks)
    {
        if (lead is null || string.IsNullOrWhiteSpace(lead.ParentSelector) || allLinks is null)
        {
            return null;
        }

        var candidates = allLinks
            .Where(l => !l.IsGroupHeader && !string.IsNullOrWhiteSpace(l.ParentSelector))
            .ToList();

        static bool IsStory(LinkInfo l) =>
            l.Type == LinkType.Content && !l.IsSponsored && (l.DisplayText?.Length ?? 0) >= MinStoryTextLength;

        var stories = candidates.Where(IsStory).ToList();

        // The lead is a story by the user's declaration, even if it fell below a heuristic floor.
        if (!stories.Any(l => ReferenceEquals(l, lead) || string.Equals(l.Url, lead.Url, StringComparison.Ordinal)))
        {
            stories.Add(lead);
        }

        var nonStory = candidates.Where(l => !IsStory(l)).ToList();

        var leadTokens = SelectorDerivation.DiscriminatingTokens(lead.ParentSelector)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (leadTokens.Count == 0)
        {
            return null;
        }

        bool Matches(LinkInfo l, string token) =>
            l.ParentSelector!.Contains(token, StringComparison.OrdinalIgnoreCase);

        string? best = null;
        double bestScore = -1;
        var bestHits = 0;
        foreach (var token in leadTokens)
        {
            var s = stories.Count(l => Matches(l, token));
            if (s == 0)
            {
                continue; // must at least cover the lead's story set
            }

            var n = nonStory.Count(l => Matches(l, token));
            var precision = (double)s / (s + n);
            var recall = (double)s / stories.Count;
            var score = precision * recall;

            // Higher score wins; tie-break toward more stories, then the more
            // specific (longer) token so the river is as targeted as possible.
            if (score > bestScore + 1e-9 ||
                (Math.Abs(score - bestScore) <= 1e-9 && (s > bestHits || (s == bestHits && token.Length > (best?.Length ?? 0)))))
            {
                bestScore = score;
                best = token;
                bestHits = s;
            }
        }

        if (best is null)
        {
            return null;
        }

        // Any non-story the river token still sweeps in: exclude its distinctive
        // tokens, but ONLY tokens that match no story (so an exclude can never
        // erase the river).
        var leaked = nonStory.Where(l => Matches(l, best)).ToList();
        var excludes = leaked
            .SelectMany(l => SelectorDerivation.DiscriminatingTokens(l.ParentSelector))
            .Distinct(StringComparer.Ordinal)
            .Where(t => !string.Equals(t, best, StringComparison.OrdinalIgnoreCase))
            .Where(t => stories.All(s => !Matches(s, t)))
            .Take(4)
            .ToList();

        return new Result
        {
            RiverSelectors = new List<string> { best },
            ExcludeSelectors = excludes,
            StoryMatchCount = bestHits,
        };
    }

    internal sealed record Result
    {
        /// <summary>The generalized river selector(s) — matches the lead and the sibling stories.</summary>
        public required List<string> RiverSelectors { get; init; }

        /// <summary>Noise containers the river token also sweeps in but that match no story (safe to exclude).</summary>
        public required List<string> ExcludeSelectors { get; init; }

        /// <summary>How many story-shaped links the river selector matches (always &gt;= 1: the lead).</summary>
        public required int StoryMatchCount { get; init; }
    }
}
