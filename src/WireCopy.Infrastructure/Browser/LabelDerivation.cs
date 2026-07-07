// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
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
    /// <summary>
    /// workspace-t1ok.5: at most this many distinct rivers derive from one label
    /// session; further uncovered articles pin by exact path into the first river.
    /// </summary>
    internal const int MaxLabelRivers = 6;

    /// <summary>
    /// workspace-t1ok.5: the deterministic label → durable-config derivation —
    /// ZERO model calls when the page's structure repeats. Ranked articles seed
    /// story rivers (<see cref="LeadOverrideDerivation"/>), one river per
    /// container cluster, ordered by the lowest labeled rank each contains;
    /// an article whose selector doesn't generalize still appears via an
    /// exact-path section (honest, today-durable). Ads/ignores become exclude
    /// rules (<see cref="DeriveExcludeFor"/>, exact-path fallback so a labeled
    /// ad is ALWAYS hidden); menu labels become More-menu routes. A final
    /// protection pass drops any rule that would hide a labeled article, and
    /// the ledger merges in so the corrections survive every later AI round.
    /// Returns null when there is nothing to derive (no labels at all).
    /// </summary>
    internal static SiteHierarchyConfig? DeriveConfig(
        SiteHierarchyConfig current,
        IReadOnlyList<UserLinkLabel> labels,
        IReadOnlyCollection<string> seenUrls,
        List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(links);
        if (labels.Count == 0 && current.UserLabels.Count == 0)
        {
            return null;
        }

        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var byKey = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        foreach (var link in contentLinks)
        {
            byKey.TryAdd(NormalizeUrl(link.Url), link);
        }

        LinkInfo? Resolve(UserLinkLabel label) =>
            byKey.TryGetValue(NormalizeUrl(label.Url), out var l) ? l : null;

        List<(UserLinkLabel Label, LinkInfo Link)> Present(LinkLabelKind kind) => labels
            .Where(l => l.Kind == kind)
            .Select(l => (Label: l, Link: Resolve(l)))
            .Where(x => x.Link != null)
            .Select(x => (x.Label, x.Link!))
            .ToList();

        var articles = Present(LinkLabelKind.Article).OrderBy(x => x.Label.Rank ?? int.MaxValue).ToList();
        var articleLinks = articles.Select(x => x.Link).ToList();

        // ---- ranked articles → story rivers ----
        var rivers = new List<(HierarchySection Section, int MinRank)>();
        var riverLeakExcludes = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var riverIndex = current.Sections.Count(s => IsRiverName(s.Name));
        foreach (var (label, link) in articles)
        {
            if (covered.Contains(NormalizeUrl(link.Url)))
            {
                continue;
            }

            var rank = label.Rank ?? int.MaxValue;

            // workspace-nbvb.1: an article already inside an existing river
            // section keeps THAT river — its rank orders it there via
            // OrderByLabeledRank. Re-deriving here rebuilt a fresh (sometimes
            // DIFFERENT) river named "Stories 2" on every later mark, so
            // hiding one link could restructure the whole layout. A river the
            // user RENAMED (nbvb.4) no longer carries a river name, so the
            // rename ledger vouches for it instead.
            var existingRiver = current.Sections.FirstOrDefault(s =>
                (IsRiverName(s.Name) || IsRenamedSection(current, s))
                && NavigationTreeBuilder.MatchesSection(link, s));
            if (existingRiver != null)
            {
                var slot = rivers.FindIndex(r => ReferenceEquals(r.Section, existingRiver));
                if (slot >= 0)
                {
                    rivers[slot] = (existingRiver, Math.Min(rivers[slot].MinRank, rank));
                }
                else
                {
                    rivers.Add((existingRiver, rank));
                }

                foreach (var other in contentLinks.Where(l => NavigationTreeBuilder.MatchesSection(l, existingRiver)))
                {
                    covered.Add(NormalizeUrl(other.Url));
                }

                continue;
            }

            if (rivers.Count >= MaxLabelRivers && rivers.Count > 0)
            {
                // Bound reached: pin by exact path into the leading river.
                var (lead, minRank) = rivers[0];
                rivers[0] = (lead with
                {
                    UrlPatterns = lead.UrlPatterns.Append(NormalizeUrl(link.Url)).Distinct(StringComparer.Ordinal).ToList(),
                }, Math.Min(minRank, rank));
                covered.Add(NormalizeUrl(link.Url));
                continue;
            }

            HierarchySection section;
            var deriv = LeadOverrideDerivation.Derive(link, links);
            if (deriv is { StoryMatchCount: > 0 })
            {
                riverIndex++;
                section = new HierarchySection
                {
                    Name = riverIndex == 1 ? "Stories" : $"Stories {riverIndex}",
                    SortOrder = 0,
                    ParentSelectors = deriv.RiverSelectors,
                };
                riverLeakExcludes.AddRange(deriv.ExcludeSelectors);
            }
            else
            {
                // The selector doesn't generalize — the labeled article still must
                // appear: an exact-path section (today-durable, honest).
                riverIndex++;
                section = new HierarchySection
                {
                    Name = riverIndex == 1 ? "Stories" : $"Stories {riverIndex}",
                    SortOrder = 0,
                    UrlPatterns = new List<string> { NormalizeUrl(link.Url) },
                };
            }

            rivers.Add((section, rank));
            foreach (var other in articleLinks.Where(l => NavigationTreeBuilder.MatchesSection(l, section)))
            {
                covered.Add(NormalizeUrl(other.Url));
            }
        }

        // ---- merge with the current sections (mirror the pick-merge semantics:
        // keep prior sections still covering stories the rivers don't; drop the
        // subsumed; rivers order by the lowest labeled rank they contain) ----
        var sections = current.Sections;
        if (rivers.Count > 0)
        {
            var orderedRivers = rivers.OrderBy(r => r.MinRank).Select(r => r.Section).ToList();
            var riverCovered = contentLinks
                .Where(l => orderedRivers.Any(r => NavigationTreeBuilder.MatchesSection(l, r)))
                .ToHashSet();
            var preserved = current.Sections
                .Where(sec => contentLinks.Any(l => !riverCovered.Contains(l) && NavigationTreeBuilder.MatchesSection(l, sec)))
                .ToList();
            sections = orderedRivers.Concat(preserved).ToList();
            for (var i = 0; i < sections.Count; i++)
            {
                sections[i] = sections[i] with { SortOrder = i };
            }
        }

        var config = current with
        {
            Sections = sections,
            ExcludeSelectors = current.ExcludeSelectors
                .Concat(riverLeakExcludes)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };

        config = ApplyRuleLabels(config, labels, links);

        // workspace-nbvb.4: freshly built rivers must pick up the user's
        // section names before they render.
        config = ApplySectionRenames(config);

        return config with
        {
            Kind = config.Sections.Count > 0 ? LayoutKind.AiCurated : current.Kind,
            Strategy = config.Sections.Count > 0 ? ScrapingStrategies.AiCuratedStrategy.StrategyId : current.Strategy,
            ModelVersion = string.IsNullOrEmpty(current.ModelVersion) ? "labels" : $"{current.ModelVersion}+labels",
            NeedsReanalyze = false,
            Version = 3,
            UserLabels = MergeLabels(current.UserLabels, labels, seenUrls),
        };
    }

    /// <summary>
    /// workspace-t1ok.8: the standard treatment for EVERY accepted model round —
    /// carry the user's durable state onto the fresh config, then re-apply the
    /// ledger's rule labels in code. Prompting the model to honor the labels
    /// (which the refine/label prompts do) is advisory; this is the enforcement:
    /// a disobedient response cannot resurrect a labeled ad, strand a menu link
    /// in the story flow, or hide a labeled article.
    /// </summary>
    internal static SiteHierarchyConfig CarryAndEnforce(
        SiteHierarchyConfig fresh,
        SiteHierarchyConfig? prior,
        List<LinkInfo> links)
    {
        var carried = CarryUserState(fresh, prior);
        if (carried.UserLabels.Count > 0)
        {
            carried = ApplyRuleLabels(carried, carried.UserLabels, links);
        }

        // workspace-nbvb.4: user renames are enforced like labels — the model
        // rewrites section names freely; the ledger wins.
        return ApplySectionRenames(carried);
    }

    /// <summary>
    /// workspace-t1ok.8: records a plain-English instruction as APPLIED on the
    /// config it produced (oldest first, capped) — later refine prompts carry
    /// the log so a new tweak can't silently undo an earlier one.
    /// </summary>
    internal static SiteHierarchyConfig AppendInstruction(SiteHierarchyConfig config, string instruction)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return config;
        }

        var log = config.UserInstructions.Append(instruction.Trim()).ToList();
        if (log.Count > SiteHierarchyConfig.MaxUserInstructions)
        {
            log = log.Skip(log.Count - SiteHierarchyConfig.MaxUserInstructions).ToList();
        }

        return config with { UserInstructions = log };
    }

    /// <summary>
    /// workspace-t1ok.5/.6: applies the RULE labels (ads, ignores, menus) plus
    /// the article-protection pass to a config — used by
    /// <see cref="DeriveConfig"/> AND re-applied in code on top of every model
    /// fallback/refine result, so a disobedient model can never resurrect a
    /// labeled ad or strand a menu link back in the story flow.
    /// </summary>
    internal static SiteHierarchyConfig ApplyRuleLabels(
        SiteHierarchyConfig config,
        IReadOnlyList<UserLinkLabel> labels,
        List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(links);
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var byKey = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        foreach (var link in contentLinks)
        {
            byKey.TryAdd(NormalizeUrl(link.Url), link);
        }

        List<LinkInfo> Present(LinkLabelKind kind) => labels
            .Where(l => l.Kind == kind)
            .Select(l => byKey.TryGetValue(NormalizeUrl(l.Url), out var link) ? link : null)
            .Where(l => l != null)
            .Select(l => l!)
            .ToList();

        // ---- ads / ignores → durable exclude rules (exact-path fallback: a
        // labeled ad is ALWAYS hidden, even when nothing generalizes) ----
        foreach (var link in Present(LinkLabelKind.Ad))
        {
            config = DeriveExcludeFor(config, link, links, allowSectionTitle: true)
                ?? AppendExactExclude(config, link, links);
        }

        foreach (var link in Present(LinkLabelKind.Ignore))
        {
            // workspace-nbvb.1: 'i' hides EXACTLY this link — the exact rule
            // comes first; class extrapolation is the Ad label's job. The
            // token/segment route remains only for the URL-prefix collision
            // where an exact rule would nuke sibling links.
            var exact = AppendExactExclude(config, link, links);
            config = NavigationTreeBuilder.IsExcluded(link, exact)
                ? exact
                : DeriveExcludeFor(config, link, links, allowSectionTitle: false) ?? exact;
        }

        // ---- menu labels → More-menu routes ----
        var menuLinks = Present(LinkLabelKind.Menu);
        if (menuLinks.Count > 0)
        {
            var menuKeys = menuLinks.Select(l => NormalizeUrl(l.Url)).ToHashSet(StringComparer.Ordinal);
            bool SafeMenuFragment(string frag) => contentLinks.All(l =>
                menuKeys.Contains(NormalizeUrl(l.Url))
                || !IsStoryShaped(l)
                || string.IsNullOrEmpty(l.ParentSelector)
                || !l.ParentSelector.Contains(frag, StringComparison.OrdinalIgnoreCase));

            var moreSelectors = new List<string>();
            var morePatterns = new List<string>();
            var shared = SelectorDerivation.DeriveParentSelectors(menuLinks).Where(SafeMenuFragment).ToList();
            if (shared.Count > 0)
            {
                moreSelectors.AddRange(shared);
            }
            else
            {
                foreach (var link in menuLinks)
                {
                    var token = SelectorDerivation.DiscriminatingTokens(link.ParentSelector)
                        .FirstOrDefault(SafeMenuFragment);
                    if (token != null)
                    {
                        moreSelectors.Add(token);
                    }
                    else
                    {
                        morePatterns.Add(NormalizeUrl(link.Url));
                    }
                }
            }

            config = config with
            {
                MoreSelectors = config.MoreSelectors.Concat(moreSelectors).Distinct(StringComparer.Ordinal).ToList(),
                MoreUrlPatterns = config.MoreUrlPatterns.Concat(morePatterns).Distinct(StringComparer.Ordinal).ToList(),
            };
        }

        // ---- article protection: no rule may hide or reroute a labeled article ----
        var articleLinks = Present(LinkLabelKind.Article);
        if (articleLinks.Count > 0)
        {
            config = config with
            {
                ExcludeSelectors = config.ExcludeSelectors
                    .Where(sel => articleLinks.All(a => a.ParentSelector?.Contains(sel, StringComparison.OrdinalIgnoreCase) != true))
                    .ToList(),
                ExcludeUrlPatterns = config.ExcludeUrlPatterns
                    .Where(pat => articleLinks.All(a => !a.Url.Contains(pat, StringComparison.OrdinalIgnoreCase)))
                    .ToList(),
                ExcludeSectionTitles = config.ExcludeSectionTitles
                    .Where(title => articleLinks.All(a => !string.Equals(a.SectionTitle, title, StringComparison.OrdinalIgnoreCase)))
                    .ToList(),
                MoreSelectors = config.MoreSelectors
                    .Where(sel => articleLinks.All(a => a.ParentSelector?.Contains(sel, StringComparison.OrdinalIgnoreCase) != true))
                    .ToList(),
                MoreUrlPatterns = config.MoreUrlPatterns
                    .Where(pat => articleLinks.All(a => !a.Url.Contains(pat, StringComparison.OrdinalIgnoreCase)))
                    .ToList(),
            };
        }

        return config;
    }

    /// <summary>
    /// workspace-t1ok.5: the label self-test — does <paramref name="config"/>
    /// actually reproduce the user's labels on this page's links? Empty = pass.
    /// Only labels whose URL is still on the page are asserted; each failure is
    /// a human-readable sentence (they feed the AI-fallback prompt and logs).
    /// </summary>
    internal static IReadOnlyList<string> LabelsReproducedFailures(
        SiteHierarchyConfig config,
        IReadOnlyList<UserLinkLabel> labels,
        List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(links);
        var failures = new List<string>();
        var byKey = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        foreach (var link in links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader))
        {
            byKey.TryAdd(NormalizeUrl(link.Url), link);
        }

        foreach (var label in labels)
        {
            if (!byKey.TryGetValue(NormalizeUrl(label.Url), out var link))
            {
                continue; // rotated off the page — nothing to assert today
            }

            var text = link.DisplayText.Length > 44 ? link.DisplayText[..44] + "…" : link.DisplayText;
            switch (label.Kind)
            {
                case LinkLabelKind.Article:
                    if (NavigationTreeBuilder.IsExcluded(link, config))
                    {
                        failures.Add($"article \"{text}\" is hidden by an exclude rule");
                    }
                    else if (NavigationTreeBuilder.MatchesMore(link, config))
                    {
                        failures.Add($"article \"{text}\" is routed into the More menu");
                    }
                    else if (config.Sections.Count > 0
                        && !config.Sections.Any(sec => NavigationTreeBuilder.MatchesSection(link, sec)))
                    {
                        failures.Add($"article \"{text}\" is not captured by any section");
                    }

                    break;
                case LinkLabelKind.Ad or LinkLabelKind.Ignore:
                    if (!NavigationTreeBuilder.IsExcluded(link, config))
                    {
                        failures.Add($"hidden link \"{text}\" is still visible");
                    }

                    break;
                case LinkLabelKind.Menu:
                    if (NavigationTreeBuilder.IsExcluded(link, config))
                    {
                        failures.Add($"menu link \"{text}\" is excluded instead of routed to More");
                    }
                    else if (!NavigationTreeBuilder.MatchesMore(link, config))
                    {
                        failures.Add($"menu link \"{text}\" is not routed to the More menu");
                    }

                    break;
            }
        }

        return failures;
    }

    /// <summary>
    /// workspace-5vqk.4 (moved from SetupWizard.ExcludeItem for workspace-t1ok.5):
    /// deterministically excludes ONE link, extrapolating to its class where safe.
    /// Routes: (1) a recognized ad/rail HEADING hides the whole heading (the
    /// durable techmeme-style ad kill; gated by <paramref name="allowSectionTitle"/> —
    /// an Ignore label hides just the link, never a heading wholesale);
    /// (2) story-safe selector tokens; (3) distinctive URL segments hitting no
    /// kept story. Returns null when nothing distinctive spares the kept stories
    /// (callers with explicit user intent fall back to an exact-path rule).
    /// </summary>
    internal static SiteHierarchyConfig? DeriveExcludeFor(
        SiteHierarchyConfig config,
        LinkInfo item,
        List<LinkInfo> links,
        bool allowSectionTitle)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(item);
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();

        // workspace-rpop.4: the DURABLE, class-level exclude. If the removed item sits
        // under a recognized ad/rail heading ("Sponsor Posts", "Featured Podcasts", …
        // captured by rpop.1), hide the WHOLE heading. The heading text is stable
        // across revisits, so — unlike a selector token that on techmeme resolves to
        // a per-day RANDOM sponsor-block id — the ad stays removed, and it catches
        // EVERY ad in that section regardless of its markup. This is exactly "remove
        // one ad, extrapolate to the rest", keyed on what the page SHOWS (a heading),
        // not on brittle HTML structure.
        var heading = item.SectionTitle?.Trim();
        if (allowSectionTitle
            && !string.IsNullOrEmpty(heading)
            && !config.ExcludeSectionTitles.Contains(heading, StringComparer.OrdinalIgnoreCase)
            && OpenAiHierarchyAnalyzer.IsNonStoryRailSectionName(heading))
        {
            return config with
            {
                ExcludeSectionTitles = config.ExcludeSectionTitles
                    .Append(heading!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        // workspace-r8on: EVERY story-shaped link on the page except the item — an
        // exclude token/pattern may never match one of these. Protecting ALL story-
        // shaped links (not just the ones a section currently COVERS) is what makes
        // "remove an ad, extrapolate to other ads" precise: an ad camouflaged inside
        // the news markup (techmeme's sponsor block div#culylwihx sits under div.ii)
        // has only its sponsor-block container as a story-free token, so 'x' excludes
        // exactly the ad class — NOT the whole column (div#topcol2 also matches the
        // podcast/event clusters, which are uncovered-but-real, so it stays protected).
        var keptStories = contentLinks
            .Where(l => IsStoryShaped(l)
                && !ReferenceEquals(l, item)
                && !string.Equals(l.Url, item.Url, StringComparison.Ordinal))
            .ToList();

        bool SafeToken(string t) =>
            keptStories.All(s => string.IsNullOrEmpty(s.ParentSelector)
                || !s.ParentSelector.Contains(t, StringComparison.OrdinalIgnoreCase));
        bool SafePattern(string p) =>
            keptStories.All(s => !s.Url.Contains(p, StringComparison.OrdinalIgnoreCase));

        var tokens = SelectorDerivation.DiscriminatingTokens(item.ParentSelector)
            .Where(SafeToken)
            .Distinct(StringComparer.Ordinal)
            .Where(t => !config.ExcludeSelectors.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var urlPatterns = new List<string>();
        if (tokens.Count == 0)
        {
            // No selector token separates the item from the kept stories — try a
            // distinctive URL segment that hits no kept story instead.
            urlPatterns = SelectorDerivation.MeaningfulPathSegments(item.Url)
                .Select(seg => $"/{seg}/")
                .Where(SafePattern)
                .Where(p => !config.ExcludeUrlPatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList();
        }

        if (tokens.Count == 0 && urlPatterns.Count == 0)
        {
            return null; // REFUSED — nothing distinctive that spares the kept stories
        }

        var candidate = config with
        {
            ExcludeSelectors = config.ExcludeSelectors.Concat(tokens).Distinct(StringComparer.Ordinal).ToList(),
            ExcludeUrlPatterns = config.ExcludeUrlPatterns.Concat(urlPatterns).Distinct(StringComparer.Ordinal).ToList(),
        };

        // Backstop: the 25% high-importance cap drops any rule that turns out to be
        // too broad on THIS page (belt-and-suspenders over the per-token guard).
        var guarded = NavigationTreeBuilder.GuardExcludeRules(candidate, contentLinks);
        var dropped = (candidate.ExcludeSelectors.Count - guarded.ExcludeSelectors.Count)
            + (candidate.ExcludeUrlPatterns.Count - guarded.ExcludeUrlPatterns.Count);

        // If the guard vetoed everything we added, the item is still shown — refuse.
        if (!NavigationTreeBuilder.IsExcluded(item, guarded))
        {
            return null;
        }

        return guarded with { DroppedExcludeRuleCount = config.DroppedExcludeRuleCount + dropped };
    }

    /// <summary>
    /// The last-resort fallback for an explicit user label: an exact
    /// normalized-URL exclude pattern — normally hides exactly one link, so it
    /// can't trip the proportional guard. Exclude rules match by SUBSTRING,
    /// though, so a short URL that PREFIXES other links' URLs (x.com/news vs
    /// x.com/news/alpha) would nuke them — in that case the rule is refused and
    /// the miss surfaces honestly through the label self-test instead.
    /// Today-durable by design (the heading/token routes are the generalizing
    /// ones).
    /// </summary>
    internal static SiteHierarchyConfig AppendExactExclude(SiteHierarchyConfig config, LinkInfo item, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(links);
        var exact = NormalizeUrl(item.Url);
        if (exact.Length == 0 || config.ExcludeUrlPatterns.Contains(exact, StringComparer.OrdinalIgnoreCase))
        {
            return config;
        }

        var itemKey = NormalizeUrl(item.Url);
        var collides = links.Any(l => l.Type == LinkType.Content
            && !l.IsGroupHeader
            && NormalizeUrl(l.Url) != itemKey
            && l.Url.Contains(exact, StringComparison.OrdinalIgnoreCase));
        if (collides)
        {
            return config;
        }

        return config with
        {
            ExcludeUrlPatterns = config.ExcludeUrlPatterns.Append(exact).ToList(),
        };
    }

    /// <summary>Real headline shape (mirrors LeadOverrideDerivation's story test).</summary>
    internal static bool IsStoryShaped(LinkInfo l) =>
        l.Type == LinkType.Content && !l.IsSponsored
        && (l.DisplayText?.Length ?? 0) >= LeadOverrideDerivation.MinStoryTextLength;

    /// <summary>True for a section name a pick/label river produced ("Stories", "Stories 2", …).</summary>
    internal static bool IsRiverName(string name) =>
        string.Equals(name, "Stories", StringComparison.Ordinal)
        || name.StartsWith("Stories ", StringComparison.Ordinal);

    /// <summary>workspace-nbvb.4: true when the rename ledger owns this section (identifier overlap).</summary>
    internal static bool IsRenamedSection(SiteHierarchyConfig config, HierarchySection section) =>
        config.UserSectionNames.Any(r => section.ParentSelectors.Concat(section.UrlPatterns)
            .Intersect(r.Identifiers, StringComparer.OrdinalIgnoreCase)
            .Any());

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
            UserSectionNames = fresh.UserSectionNames.Count > 0 ? fresh.UserSectionNames : prior.UserSectionNames,
            MoreSelectors = fresh.MoreSelectors.Count > 0 ? fresh.MoreSelectors : prior.MoreSelectors,
            MoreUrlPatterns = fresh.MoreUrlPatterns.Count > 0 ? fresh.MoreUrlPatterns : prior.MoreUrlPatterns,
        };
    }

    /// <summary>
    /// workspace-nbvb.4: records a user's section rename on the ledger (newest
    /// last, latest-wins when the same identifiers are renamed again, capped)
    /// and applies it to the section list immediately.
    /// </summary>
    internal static SiteHierarchyConfig AppendSectionRename(
        SiteHierarchyConfig config, HierarchySection section, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(section);
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return config;
        }

        var identifiers = section.ParentSelectors.Concat(section.UrlPatterns)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var entry = new UserSectionRename
        {
            Identifiers = identifiers,
            Name = trimmed,
            RenamedAt = DateTime.UtcNow,
        };

        // Latest-wins: renaming the same section again replaces the old entry.
        var ledger = config.UserSectionNames
            .Where(r => !r.Identifiers.Intersect(identifiers, StringComparer.OrdinalIgnoreCase).Any())
            .Append(entry)
            .ToList();
        if (ledger.Count > SiteHierarchyConfig.MaxUserSectionNames)
        {
            ledger = ledger.Skip(ledger.Count - SiteHierarchyConfig.MaxUserSectionNames).ToList();
        }

        return ApplySectionRenames(config with { UserSectionNames = ledger });
    }

    /// <summary>
    /// workspace-nbvb.4: re-applies the rename ledger to the CURRENT section
    /// list — every model round and label derivation builds fresh sections, so
    /// this runs at the same enforcement points the labels use
    /// (<see cref="CarryAndEnforce"/> and the end of <see cref="DeriveConfig"/>).
    /// A rename owns the FIRST section sharing any of its identifiers; each
    /// section takes at most one rename.
    /// </summary>
    internal static SiteHierarchyConfig ApplySectionRenames(SiteHierarchyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.UserSectionNames.Count == 0 || config.Sections.Count == 0)
        {
            return config;
        }

        var sections = config.Sections.ToList();
        var renamedIndexes = new HashSet<int>();
        foreach (var rename in config.UserSectionNames)
        {
            for (var i = 0; i < sections.Count; i++)
            {
                if (renamedIndexes.Contains(i))
                {
                    continue;
                }

                var identifiers = sections[i].ParentSelectors.Concat(sections[i].UrlPatterns);
                if (identifiers.Intersect(rename.Identifiers, StringComparer.OrdinalIgnoreCase).Any())
                {
                    if (!string.Equals(sections[i].Name, rename.Name, StringComparison.Ordinal))
                    {
                        sections[i] = sections[i] with { Name = rename.Name };
                    }

                    renamedIndexes.Add(i);
                    break;
                }
            }
        }

        return renamedIndexes.Count == 0 ? config : config with { Sections = sections };
    }
}
