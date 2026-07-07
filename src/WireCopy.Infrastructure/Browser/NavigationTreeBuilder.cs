// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Builds hierarchical navigation trees from extracted links.
/// </summary>
public class NavigationTreeBuilder : INavigationTreeBuilder
{
    internal const int MaxContentLinks = 100;

    // workspace-gyw5: a Content group whose links are AT LEAST this fraction off-domain is an
    // aggregator river (HN, Techmeme) whose DOM order IS the editorial rank and whose importance
    // score does NOT track it — leave it entirely in DOM order. Below this fraction the page is a
    // publisher (NYT) that may float low-value chrome into its lead, which we demote.
    internal const double AggregatorExternalContentFraction = 0.5;

    // workspace-gyw5: on a publisher page, Content links scoring BELOW this are below-fold /
    // low-text promo, podcast, newsletter and column-hub chrome — never the lead stories. Real
    // above-fold headlines clear it (base 70 + text + above-fold/font geometry boosts push them to
    // 88-100; observed NYT chrome tops out at 85, observed NYT/danluu lead stories are >=88). Used
    // only to find where a LEADING chrome block ends (see OrderAndCapContentLinks); the exact value
    // matters little as long as the leading promos fall below it and the first real story clears it.
    internal const int LeadImportanceFloor = 86;

    // workspace-2k28: header for the trailing section that holds demoted null-titled chrome when
    // SectionTitle sub-grouping is active (headerless links would otherwise render FIRST and
    // re-hoist the chrome above the sectioned stories).
    internal const string DemotedChromeSectionTitle = "More links";

    private readonly ILogger<NavigationTreeBuilder> _logger;

    public NavigationTreeBuilder(ILogger<NavigationTreeBuilder> logger)
    {
        _logger = logger;
    }

    public Task<NavigationTree> BuildTreeAsync(List<LinkInfo> links, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(BuildGroupedTree(links));
    }

    public Task<NavigationTree> BuildTreeAsync(
        List<LinkInfo> links,
        SiteHierarchyConfig hierarchyConfig,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(BuildWithHierarchyConfig(links, hierarchyConfig));
    }

    public Task<NavigationTree> BuildFromAiResultAsync(
        List<LinkInfo> links,
        AiCuratedResult curated,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(BuildFromAiCuratedResult(links, curated));
    }

    public NavigationTree BuildGroupedTree(List<LinkInfo> links)
    {
        var grouped = links
            .GroupBy(l => l.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (grouped.TryGetValue(LinkType.Content, out var contentLinks) && contentLinks.Count > 0)
        {
            grouped[LinkType.Content] = OrderAndCapContentLinks(contentLinks);
        }

        var tree = NavigationTree.BuildWithGroups(grouped);

        _logger.LogInformation(
            "Built navigation tree with {Total} links: {Content} content, {Nav} navigation, {External} external, {Footer} footer",
            links.Count,
            grouped.GetValueOrDefault(LinkType.Content)?.Count ?? 0,
            grouped.GetValueOrDefault(LinkType.Navigation)?.Count ?? 0,
            grouped.GetValueOrDefault(LinkType.External)?.Count ?? 0,
            grouped.GetValueOrDefault(LinkType.Footer)?.Count ?? 0);

        return tree;
    }

    /// <summary>
    /// workspace-9k27.1: build-time twin of the analyzer's parse-time guard.
    /// Evaluates each exclude rule against THIS visit's high-importance,
    /// non-sponsored links and skips any rule that would hide more than
    /// <see cref="SiteHierarchyConfig.MaxExcludeHighImportanceFraction"/> of
    /// them — a rule that broad is hiding stories, not chrome. Sponsored links
    /// are left out of the numerator AND denominator so a legitimately large
    /// sponsor section can still be excluded in full.
    /// </summary>
    internal static SiteHierarchyConfig GuardExcludeRules(SiteHierarchyConfig config, List<LinkInfo> contentLinks, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var highScore = contentLinks
            .Where(l => l.ImportanceScore >= SiteHierarchyConfig.HighImportanceScoreThreshold
                && !l.IsGroupHeader
                && !l.IsSponsored)
            .ToList();
        if (highScore.Count == 0)
        {
            return config;
        }

        // Floor of 2: on a small page any single exclusion exceeds the fraction,
        // which would veto perfectly surgical rules. Percentages only mean
        // something with enough links to take a percentage of.
        var maxHidden = Math.Max(2.0, highScore.Count * SiteHierarchyConfig.MaxExcludeHighImportanceFraction);

        var keptSelectors = config.ExcludeSelectors
            .Where(sel => string.IsNullOrEmpty(sel) || highScore.Count(l =>
                !string.IsNullOrEmpty(l.ParentSelector)
                && l.ParentSelector.Contains(sel, StringComparison.OrdinalIgnoreCase)) <= maxHidden)
            .ToList();
        var keptPatterns = config.ExcludeUrlPatterns
            .Where(pat => string.IsNullOrEmpty(pat) || highScore.Count(l =>
                l.Url.Contains(pat, StringComparison.OrdinalIgnoreCase)) <= maxHidden)
            .ToList();

        var droppedCount = (config.ExcludeSelectors.Count - keptSelectors.Count)
            + (config.ExcludeUrlPatterns.Count - keptPatterns.Count);
        if (droppedCount == 0)
        {
            return config;
        }

        logger?.LogWarning(
            "Skipped {Count} saved exclude rule(s) this visit: each would hide >{Max:P0} of the page's {High} high-importance links (rule broadened since save — workspace-9k27.1)",
            droppedCount,
            SiteHierarchyConfig.MaxExcludeHighImportanceFraction,
            highScore.Count);
        return config with { ExcludeSelectors = keptSelectors, ExcludeUrlPatterns = keptPatterns };
    }

    /// <summary>
    /// workspace-5oe9.1: durable exclusion test. A content link is dropped when
    /// its <see cref="LinkInfo.ParentSelector"/> contains any configured
    /// <see cref="SiteHierarchyConfig.ExcludeSelectors"/> fragment, or its
    /// <see cref="LinkInfo.Url"/> contains any
    /// <see cref="SiteHierarchyConfig.ExcludeUrlPatterns"/> substring
    /// (OrdinalIgnoreCase, mirroring <see cref="MatchesSection"/>).
    /// </summary>
    internal static bool IsExcluded(LinkInfo link, SiteHierarchyConfig config)
    {
        if (link.ParentSelector != null && config.ExcludeSelectors.Count > 0 &&
            config.ExcludeSelectors.Any(s =>
                !string.IsNullOrEmpty(s) &&
                link.ParentSelector.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (config.ExcludeUrlPatterns.Count > 0 &&
            config.ExcludeUrlPatterns.Any(p =>
                !string.IsNullOrEmpty(p) &&
                link.Url.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // workspace-rpop.4: durable ad/rail exclusion by section heading.
        if (link.SectionTitle != null && config.ExcludeSectionTitles.Count > 0 &&
            config.ExcludeSectionTitles.Any(t =>
                !string.IsNullOrEmpty(t) &&
                string.Equals(link.SectionTitle, t, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// workspace-cn2g.5: order links by a rank encoded in their URL (a small numeric
    /// path segment like /1 /2 /3, or a rank/order/pos/position/page/p query param),
    /// when the signal is CLEAN — enough links carry a rank, the ranks are mostly
    /// distinct, and they stay in a plausible 1..N band (not scattered ids). Returns
    /// null when there's no such structure, so the caller falls back to DOM/importance
    /// order. Ranked links come first (ascending); any unranked keep their relative order.
    /// </summary>
    internal static List<LinkInfo>? TryOrderByUrlRank(IReadOnlyList<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var stories = links.Where(l => !l.IsGroupHeader && !string.IsNullOrEmpty(l.Url)).ToList();
        if (stories.Count < 3)
        {
            return null;
        }

        var ranked = stories.Select(l => (Link: l, Rank: ExtractUrlRank(l.Url))).ToList();
        var withRank = ranked.Where(r => r.Rank is not null).ToList();

        // Need most links to carry a rank, and the ranks to be MOSTLY DISTINCT (a
        // shared id/category number repeated across links is not a rank).
        if (withRank.Count < Math.Max(3, (int)(stories.Count * 0.6)))
        {
            return null;
        }

        var ranks = withRank.Select(r => r.Rank!.Value).ToList();
        if (ranks.Distinct().Count() < (int)(withRank.Count * 0.8))
        {
            return null;
        }

        // A real rank list starts at 1 (allow 1 or 2), stays in a plausible band, and
        // is reasonably CONTIGUOUS — this rejects date segments (/2026/06/12 -> 12) and
        // sparse ids, which look small-and-distinct but never form a 1..N sequence.
        var min = ranks.Min();
        var max = ranks.Max();
        if (min > 2 || max > Math.Max(20, stories.Count * 3))
        {
            return null;
        }

        if (ranks.Distinct().Count() < 0.7 * (max - min + 1))
        {
            return null;
        }

        return withRank.OrderBy(r => r.Rank!.Value).Select(r => r.Link)
            .Concat(ranked.Where(r => r.Rank is null).Select(r => r.Link))
            .ToList();
    }

    /// <summary>workspace-cn2g.5: a rank integer from a URL — a rank-ish query param, else a small numeric path segment.</summary>
    internal static int? ExtractUrlRank(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // A rank-bearing query param wins (?rank=2, ?order=3, ?pos=1, ?position=4, ?page=2, ?p=5).
        if (uri.Query.Length > 1)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2
                    && (kv[0].Equals("rank", StringComparison.OrdinalIgnoreCase)
                        || kv[0].Equals("order", StringComparison.OrdinalIgnoreCase)
                        || kv[0].Equals("pos", StringComparison.OrdinalIgnoreCase)
                        || kv[0].Equals("position", StringComparison.OrdinalIgnoreCase)
                        || kv[0].Equals("page", StringComparison.OrdinalIgnoreCase)
                        || kv[0].Equals("p", StringComparison.OrdinalIgnoreCase))
                    && int.TryParse(kv[1], out var q) && q >= 1)
                {
                    return q;
                }
            }
        }

        // Else the last small purely-numeric path segment (/news/3, /2, /story/1).
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(segments[i], out var n) && n >= 1 && n <= 999)
            {
                return n;
            }
        }

        return null;
    }

    // workspace-frpl.3: MatchesSection is factored into three per-criterion
    // predicates so the scheduling SectionResolver can both MATCH and CLASSIFY
    // the match tier (Selector / UrlPattern / HeadingName) using the EXACT same
    // logic — no divergence between the tree builder and the resolver.
    internal static bool MatchesByParentSelector(LinkInfo link, HierarchySection section) =>
        link.ParentSelector != null && section.ParentSelectors.Count > 0 &&
        section.ParentSelectors.Any(s => link.ParentSelector.Contains(s, StringComparison.OrdinalIgnoreCase));

    internal static bool MatchesByUrlPattern(LinkInfo link, HierarchySection section) =>
        section.UrlPatterns.Count > 0 &&
        section.UrlPatterns.Any(p => link.Url.Contains(p, StringComparison.OrdinalIgnoreCase));

    internal static bool MatchesBySectionTitle(LinkInfo link, HierarchySection section) =>
        link.SectionTitle != null &&
        string.Equals(link.SectionTitle, section.Name, StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesSection(LinkInfo link, HierarchySection section) =>
        MatchesByParentSelector(link, section)
        || MatchesByUrlPattern(link, section)
        || MatchesBySectionTitle(link, section);

    /// <summary>
    /// workspace-gyw5: order the Content group's lead correctly (sole caller is
    /// <see cref="BuildGroupedTree"/>), then apply the <see cref="MaxContentLinks"/> cap. A
    /// PUBLISHER front page (e.g. NYT) OPENS with a block of low-value promo/podcast/newsletter/
    /// column-hub links (importance &lt;= 85) ahead of its real top-story headlines (importance
    /// &gt;= 88), so the tree led with menu/promo chrome. We move ONLY that LEADING chrome block —
    /// the sub-floor links before the first lead-grade story — to the end; everything from the
    /// first story onward keeps its exact DOM order. So a curated or reverse-chronological on-domain
    /// river (e.g. danluu.com, a section index) that opens with a real story is never reordered
    /// (workspace-2k28: a full or partition re-sort still inverted below-fold order; this does not).
    /// An AGGREGATOR river (Hacker News, Techmeme), whose DOM order IS the editorial rank and whose
    /// importance does NOT track it, is left entirely in DOM order — detected by most Content links
    /// being off-domain (<see cref="LinkInfo.IsExternal"/>). The cap then keeps the top-N.
    /// </summary>
    private List<LinkInfo> OrderAndCapContentLinks(List<LinkInfo> contentLinks)
    {
        // workspace-cn2g.5: some sites make it EASY — their URLs encode the article
        // rank (/1, /2, /3, ?rank=N). When that signal is present and clean, it is the
        // most reliable order there is, so use it as the default before any heuristic.
        var byUrlRank = TryOrderByUrlRank(contentLinks);
        if (byUrlRank != null)
        {
            _logger.LogInformation("Ordered {Count} content links by structured URL rank", byUrlRank.Count);
            return Cap(byUrlRank, "URL rank");
        }

        var externalFraction = contentLinks.Count(l => l.IsExternal) / (double)contentLinks.Count;
        var isAggregator = externalFraction >= AggregatorExternalContentFraction;

        var ordered = isAggregator ? contentLinks : DemoteLeadingChrome(contentLinks);
        return Cap(ordered, isAggregator ? "DOM rank" : "leading chrome demoted");
    }

    private List<LinkInfo> Cap(List<LinkInfo> ordered, string ordering)
    {
        if (ordered.Count > MaxContentLinks)
        {
            _logger.LogInformation(
                "Capped content links from {Total} to {Max} ({Ordering})",
                ordered.Count,
                MaxContentLinks,
                ordering);
            return ordered.Take(MaxContentLinks).ToList();
        }

        return ordered;
    }

    /// <summary>
    /// workspace-gyw5 / workspace-2k28: if the Content group OPENS with a run of sub-floor chrome
    /// (importance &lt; <see cref="LeadImportanceFloor"/>) before the first lead-grade story, move
    /// ONLY that leading run to the end; otherwise leave the group untouched. This demotes the NYT
    /// leading promo/podcast block without ever reordering a page that already opens with a real
    /// story — so curated / reverse-chronological on-domain rivers (and geometry-null pages, where
    /// nothing clears the floor) keep their exact DOM order.
    /// </summary>
#pragma warning disable SA1204 // static helper kept adjacent to its sole caller (OrderAndCapContentLinks)
    private static List<LinkInfo> DemoteLeadingChrome(List<LinkInfo> contentLinks)
    {
        var firstStory = contentLinks.FindIndex(l => l.ImportanceScore >= LeadImportanceFloor);

        // Opens with a story at index 0, or has no lead-grade link at all, so there is no leading
        // chrome block to demote — leave the group untouched.
        if (firstStory <= 0)
        {
            return contentLinks;
        }

        // Move the stories to the front and the leading chrome block behind them; both keep their
        // original DOM order.
        var stories = contentLinks.Skip(firstStory).ToList();
        var chrome = contentLinks.Take(firstStory).ToList();
        var reordered = new List<LinkInfo>(contentLinks.Count);
        reordered.AddRange(stories);
        reordered.AddRange(chrome);

        // When SectionTitle sub-grouping kicks in, the tree renders every SectionTitle==null link
        // FIRST (headerless / featured) — which would silently re-hoist demoted null-titled chrome
        // ABOVE the sectioned stories, undoing this whole demotion. Park such chrome under a
        // trailing synthetic section instead: its first occurrence is at the end of the list, so
        // it renders last (or, if the stamp tips the sub-group heuristic off, the flat demoted
        // order stands — also correct).
        if (chrome.Any(l => l.SectionTitle is null) && NavigationTree.WouldSubGroupContent(reordered))
        {
            // Never merge into a REAL section of the same name (its earlier position would win) —
            // suffix a counter until the title is unique on this page.
            var existingTitles = reordered
                .Where(l => l.SectionTitle is not null)
                .Select(l => l.SectionTitle!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var title = DemotedChromeSectionTitle;
            var suffix = 2;
            while (existingTitles.Contains(title))
            {
                title = $"{DemotedChromeSectionTitle} ({suffix})";
                suffix++;
            }

            for (var i = stories.Count; i < reordered.Count; i++)
            {
                if (reordered[i].SectionTitle is null)
                {
                    reordered[i] = reordered[i] with { SectionTitle = title };
                }
            }
        }

        return reordered;
    }
#pragma warning restore SA1204

    private NavigationTree BuildWithHierarchyConfig(List<LinkInfo> links, SiteHierarchyConfig hierarchyConfig)
    {
        // workspace-t1ok.2: a saved FLAT DocumentOrder config (Sections empty) is an
        // intentional layout — "just show every link, minus what I dropped" — not a
        // failed pattern. It used to fall through to the 0-sections greedy loop,
        // trip the stale floor below, and rebuild from UNFILTERED links: the false
        // "Saved layout no longer matches" nag on every revisit, with the user's
        // 'x'-dropped ads resurrected. Route it to the grouped/ordered tree with
        // the exclusion rules applied and no stale flag.
        if (hierarchyConfig.Sections.Count == 0 && hierarchyConfig.Kind == LayoutKind.DocumentOrder)
        {
            var flatLinks = links;
            if (hierarchyConfig.ExcludeSelectors.Count > 0
                || hierarchyConfig.ExcludeUrlPatterns.Count > 0
                || hierarchyConfig.ExcludeSectionTitles.Count > 0)
            {
                var flatContent = links.Where(l => l.Type == LinkType.Content).ToList();
                var guardedFlat = GuardExcludeRules(hierarchyConfig, flatContent, _logger);
                flatLinks = links
                    .Where(l => l.Type != LinkType.Content || !IsExcluded(l, guardedFlat))
                    .ToList();
                var flatExcluded = links.Count - flatLinks.Count;
                if (flatExcluded > 0)
                {
                    _logger.LogInformation(
                        "Flat DocumentOrder config: excluded {Count} link(s) via durable rules (workspace-t1ok.2)",
                        flatExcluded);
                }
            }

            return BuildGroupedTree(flatLinks);
        }

        var root = LinkNode.CreateRoot();
        var matchedLinks = new HashSet<int>();
        var orderedSections = hierarchyConfig.Sections.OrderBy(s => s.SortOrder).ToList();

        var allContentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        if (allContentLinks.Count > MaxContentLinks)
        {
            _logger.LogInformation(
                "Capped content links from {Total} to {Max} (AI hierarchy)",
                allContentLinks.Count,
                MaxContentLinks);
        }

        var contentLinks = allContentLinks.Count > MaxContentLinks
            ? allContentLinks.Take(MaxContentLinks).ToList()
            : allContentLinks;
        var nonContentLinks = links.Where(l => l.Type != LinkType.Content).ToList();

        // workspace-5oe9.1: apply durable exclusion rules BEFORE tiering so
        // ads/promos identified by selector/url-pattern are dropped on every
        // visit (not just the snapshot the AI was run on). Uses the same
        // Contains semantics as MatchesSection.
        // workspace-9k27.1: re-run the proportional over-exclusion guard on
        // EVERY visit, not just at save time — a selector that was surgical on
        // the save-time snapshot can broaden on a redesign and nuke the page.
        if (hierarchyConfig.ExcludeSelectors.Count > 0 || hierarchyConfig.ExcludeUrlPatterns.Count > 0)
        {
            var guarded = GuardExcludeRules(hierarchyConfig, contentLinks, _logger);
            var beforeExclude = contentLinks.Count;
            contentLinks = contentLinks.Where(l => !IsExcluded(l, guarded)).ToList();
            var excludedCount = beforeExclude - contentLinks.Count;
            if (excludedCount > 0)
            {
                _logger.LogInformation(
                    "Excluded {Count} content link(s) via durable rules ({Selectors} selectors, {UrlPatterns} url-patterns)",
                    excludedCount,
                    guarded.ExcludeSelectors.Count,
                    guarded.ExcludeUrlPatterns.Count);
            }
        }

        for (var sectionIdx = 0; sectionIdx < orderedSections.Count; sectionIdx++)
        {
            var section = orderedSections[sectionIdx];

            // workspace-9k27.3: the MaxLinks cap's "no story is ever lost" claim
            // was verified only against the SAVE-TIME snapshot. Re-verify on THIS
            // visit: honor the cap only when every overflow link is actually
            // claimed by a later section now — otherwise (a redesign broke the
            // downstream selector) lift the cap so the overflow isn't dropped.
            var effectiveCap = section.MaxLinks;
            if (section.MaxLinks is { } declaredCap)
            {
                var allMatches = new List<int>();
                for (int i = 0; i < contentLinks.Count; i++)
                {
                    if (!matchedLinks.Contains(i) && MatchesSection(contentLinks[i], section))
                    {
                        allMatches.Add(i);
                    }
                }

                var laterSections = orderedSections.Skip(sectionIdx + 1).ToList();
                var overflowClaimedDownstream = allMatches
                    .Skip(declaredCap)
                    .All(i => laterSections.Any(later => MatchesSection(contentLinks[i], later)));
                if (!overflowClaimedDownstream)
                {
                    _logger.LogWarning(
                        "Section '{Section}' cap of {Cap} lifted this visit: overflow no longer matches any later section (page layout changed) — keeping all {Matches} matches instead of dropping stories",
                        section.Name,
                        declaredCap,
                        allMatches.Count);
                    effectiveCap = null;
                }
            }

            var sectionLinks = new List<LinkInfo>();
            for (int i = 0; i < contentLinks.Count; i++)
            {
                if (matchedLinks.Contains(i))
                {
                    continue;
                }

                // workspace-9wm6: a capped section (e.g. a pinned single-story
                // lead) stops claiming once full, so the greedy loop re-offers
                // the overflow to later sections instead of dropping it.
                if (effectiveCap is { } cap && sectionLinks.Count >= cap)
                {
                    break;
                }

                var link = contentLinks[i];
                if (MatchesSection(link, section))
                {
                    sectionLinks.Add(link);
                    matchedLinks.Add(i);
                }
            }

            if (sectionLinks.Count == 0)
            {
                continue;
            }

            var sectionHeader = LinkInfo.CreateSubSectionHeader(section.Name, LinkType.Content);
            var sectionNode = root.AddChild(sectionHeader);
            if (section.StartCollapsed)
            {
                sectionNode.Collapse();
            }

            foreach (var link in sectionLinks)
            {
                sectionNode.AddChild(link);
            }
        }

        // workspace-9k27.1: staleness floor — when the saved sections cover
        // almost none of this visit's stories (site redesign killed the
        // selectors), a "curated" tree would be near-empty or empty because
        // unmatched content is dropped below. Fall back to document order and
        // FLAG it so the UI says so, instead of silently rendering nothing.
        if (contentLinks.Count > 0
            && (double)matchedLinks.Count / contentLinks.Count < SiteHierarchyConfig.StaleCoverageFraction)
        {
            _logger.LogWarning(
                "Saved AI hierarchy matched only {Covered}/{Total} content links (<{Floor:P0}) — stale config; falling back to document order (workspace-9k27.1)",
                matchedLinks.Count,
                contentLinks.Count,
                SiteHierarchyConfig.StaleCoverageFraction);
            var fallback = BuildGroupedTree(links);
            fallback.HierarchyConfigStale = true;
            return fallback;
        }

        // workspace-vwkt: drop unmatched content links rather than appending them to root.
        // The legacy AiHierarchical path used to dump unmatched links at the bottom of the
        // root node (under a vague "everything else" pile), which surfaced ad/junk links
        // beneath an AI-curated tree. The new AiCurated strategy filters destructively and
        // produces a cleaner tree; matching that semantics here keeps both paths consistent
        // and aligns with the user-visible promise of an AI-curated layout.
        var droppedUnmatched = contentLinks.Count - matchedLinks.Count;
        if (droppedUnmatched > 0)
        {
            _logger.LogInformation(
                "Dropped {Count} unmatched content link(s) from AI hierarchy tree (workspace-vwkt)",
                droppedUnmatched);
        }

        AddChromeGroups(root, nonContentLinks);

        var tree = NavigationTree.BuildFromRoot(root);
        _logger.LogInformation(
            "Built AI hierarchy tree with {Total} links in {Sections} sections (config from {Model})",
            links.Count,
            orderedSections.Count,
            hierarchyConfig.ModelVersion);
        return tree;
    }

    /// <summary>
    /// Destructive-filter builder for the AI Curated strategy.
    /// </summary>
    private NavigationTree BuildFromAiCuratedResult(List<LinkInfo> links, AiCuratedResult curated)
    {
        var excluded = new HashSet<string>(curated.ExcludedLinkKeys, StringComparer.Ordinal);

        var contentLinks = links
            .Where(l => l.Type == LinkType.Content)
            .Where(l => !excluded.Contains(AiCuratedResult.KeyFor(l.Url)))
            .ToList();

        if (contentLinks.Count > MaxContentLinks)
        {
            _logger.LogInformation("Capped curated content links from {Total} to {Max}", contentLinks.Count, MaxContentLinks);
            contentLinks = contentLinks.Take(MaxContentLinks).ToList();
        }

        var nonContentLinks = links
            .Where(l => l.Type != LinkType.Content)
            .Where(l => !excluded.Contains(AiCuratedResult.KeyFor(l.Url)))
            .ToList();

        var byKey = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        foreach (var link in contentLinks)
        {
            byKey[AiCuratedResult.KeyFor(link.Url)] = link;
        }

        var orderedKeys = curated.StoryOrderLinkKeys
            .Where(byKey.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // workspace-5oe9.13 revisit safety-net: when a snapshot's ranked keys
        // match NONE of today's links, the snapshot has decayed as article URLs
        // rotated. Log it so the staleness is visible instead of silently
        // rendering document order. The durable pattern path is the real fix.
        if (orderedKeys.Count == 0 && curated.StoryOrderLinkKeys.Count > 0)
        {
            _logger.LogWarning(
                "AI-curated snapshot matched 0 of {Total} ranked links — stale snapshot, rendering document order; reconfigure via g l",
                curated.StoryOrderLinkKeys.Count);
        }

        var orderedKeySet = new HashSet<string>(orderedKeys, StringComparer.Ordinal);

        var orderedContent = new List<LinkInfo>(contentLinks.Count);
        foreach (var key in orderedKeys)
        {
            orderedContent.Add(byKey[key]);
        }

        foreach (var link in contentLinks)
        {
            var k = AiCuratedResult.KeyFor(link.Url);
            if (!orderedKeySet.Contains(k))
            {
                orderedContent.Add(link);
            }
        }

        var root = LinkNode.CreateRoot();

        if (curated.Sections.Count > 0)
        {
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in curated.Sections)
            {
                var sectionStories = new List<LinkInfo>();
                foreach (var k in section.StoryLinkKeys)
                {
                    if (!byKey.TryGetValue(k, out var link))
                    {
                        continue;
                    }

                    if (assigned.Contains(k))
                    {
                        continue;
                    }

                    assigned.Add(k);
                    sectionStories.Add(link);
                }

                if (sectionStories.Count == 0)
                {
                    continue;
                }

                var sectionHeader = LinkInfo.CreateSubSectionHeader(section.Name, LinkType.Content);
                var sectionNode = root.AddChild(sectionHeader);
                if (section.StartCollapsed)
                {
                    sectionNode.Collapse();
                }

                foreach (var link in sectionStories)
                {
                    sectionNode.AddChild(link);
                }
            }

            foreach (var link in orderedContent.Where(l => !assigned.Contains(AiCuratedResult.KeyFor(l.Url))))
            {
                root.AddChild(link);
            }
        }
        else
        {
            foreach (var link in orderedContent)
            {
                root.AddChild(link);
            }
        }

        AddChromeGroups(root, nonContentLinks);

        var tree = NavigationTree.BuildFromRoot(root);

        _logger.LogInformation(
            "Built AI-curated tree: {Stories} stories, {Excluded} excluded, {Sections} sections",
            orderedContent.Count,
            curated.ExcludedLinkKeys.Count,
            curated.Sections.Count);

        return tree;
    }

#pragma warning disable SA1204 // helper kept adjacent to its two sole callers above (workspace-t1ok.2).
    /// <summary>
    /// workspace-t1ok.2: shared chrome-group tail for the config-driven builders,
    /// matching <see cref="NavigationTree.BuildWithGroups"/>: External keeps its
    /// own group; Navigation + Footer (+ any content links routed to the menu by
    /// config rules) consolidate into ONE collapsed
    /// <see cref="NavigationTree.MoreGroupLabel"/> sub-menu at the bottom —
    /// site chrome stays reachable but out of the article flow.
    /// </summary>
    private static void AddChromeGroups(
        LinkNode root,
        List<LinkInfo> nonContentLinks,
        IReadOnlyList<LinkInfo>? moreContent = null)
    {
        var external = nonContentLinks.Where(l => l.Type == LinkType.External).ToList();
        if (external.Count > 0)
        {
            var externalNode = root.AddChild(LinkInfo.CreateGroupHeader(LinkType.External));
            foreach (var link in external)
            {
                externalNode.AddChild(link);
            }
        }

        var more = new List<LinkInfo>();
        if (moreContent != null)
        {
            more.AddRange(moreContent);
        }

        more.AddRange(nonContentLinks.Where(l => l.Type == LinkType.Navigation));
        more.AddRange(nonContentLinks.Where(l => l.Type == LinkType.Footer));
        if (more.Count > 0)
        {
            var moreNode = root.AddChild(
                LinkInfo.CreateNamedGroupHeader(NavigationTree.MoreGroupLabel, LinkType.Navigation));
            foreach (var link in more)
            {
                moreNode.AddChild(link);
            }
        }
    }
#pragma warning restore SA1204
}
