// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser.ScrapingStrategies;

/// <summary>
/// workspace-hapr: Reasons an AI-curated result fails to actually reorder the
/// page. Surfaced in the strategy summary so the user can tell whether the
/// AI added any editorial value or whether they'd be just as well off with
/// Document Order.
/// </summary>
internal enum AiCuratedDegenerateReason
{
    /// <summary>The AI returned a non-trivial ranking that differs from doc order.</summary>
    None,

    /// <summary>StoryOrderLinkKeys is empty — every link was excluded or the AI returned nothing.</summary>
    EmptyRanking,

    /// <summary>The ranking matches the surviving document order exactly — AI ran but did not reorder.</summary>
    MatchesDocumentOrder,
}

/// <summary>
/// AI Curated scraping strategy. Sends the page links + screenshot to OpenAI
/// (workspace-65sw — previously Anthropic) and asks for an explicit
/// excluded/stories split. Excluded links are deleted from the tree (not
/// pushed down). Result is cached on <see cref="SiteHierarchyConfig.AiResult"/>
/// with a TTL.
/// </summary>
public sealed class AiCuratedStrategy : IScrapingStrategy
{
    public const string StrategyId = "AiCurated";

    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IHierarchyAnalyzer _analyzer;
    private readonly OpenAiHierarchyConfiguration _config;
    private readonly ILogger<AiCuratedStrategy> _logger;

    public AiCuratedStrategy(
        INavigationTreeBuilder treeBuilder,
        IHierarchyAnalyzer analyzer,
        IOptions<OpenAiHierarchyConfiguration> config,
        ILogger<AiCuratedStrategy> logger)
    {
        _treeBuilder = treeBuilder;
        _analyzer = analyzer;
        _config = config.Value;
        _logger = logger;
    }

    public string Id => StrategyId;

    public string DisplayName => "AI Curated";

    public string Description => "AI-curated · removes ads, ranks stories";

    public Task<ScrapingStrategyAvailability> IsAvailableAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_analyzer.IsConfigured)
        {
            return Task.FromResult(new ScrapingStrategyAvailability
            {
                IsAvailable = false,
                ReasonWhenUnavailable = "No OpenAI API key (press c on the launcher to open Setup)",
            });
        }

        // Need at least a handful of content links to bother running the AI.
        var contentCount = 0;
        foreach (var link in context.Links)
        {
            if (link.Type == Domain.Enums.Browser.LinkType.Content)
            {
                contentCount++;
                if (contentCount >= 3)
                {
                    break;
                }
            }
        }

        if (contentCount < 3)
        {
            return Task.FromResult(new ScrapingStrategyAvailability
            {
                IsAvailable = false,
                ReasonWhenUnavailable = "Page has too few content links for AI curation",
            });
        }

        var statusDetail = context.SavedConfig?.AiResult != null
            ? $"cached · {context.SavedConfig.AiResult.StoryOrderLinkKeys.Count} stories"
            : "will run on selection";

        return Task.FromResult(new ScrapingStrategyAvailability
        {
            IsAvailable = true,
            StatusDetail = statusDetail,
        });
    }

    public async Task<ScrapingStrategyResult> BuildTreeAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default)
    {
        AiCuratedResult curated;
        bool fromCache;

        var cached = context.SavedConfig?.AiResult;
        var ttl = TimeSpan.FromDays(Math.Max(1, _config.AiCuratedCacheDays));
        var requestedGuidance = NormalizeGuidance(context.UserGuidance);
        var cachedGuidance = NormalizeGuidance(cached?.UserGuidance);
        var guidanceMatches = string.Equals(cachedGuidance, requestedGuidance, StringComparison.Ordinal);

        if (cached != null && DateTime.UtcNow - cached.AnalyzedAt <= ttl && guidanceMatches)
        {
            curated = cached;
            fromCache = true;
            _logger.LogInformation(
                "AiCuratedStrategy: using cached result for {Url} ({Age} old, guidance={Guidance})",
                context.PageUrl,
                DateTime.UtcNow - cached.AnalyzedAt,
                requestedGuidance ?? "(none)");
        }
        else
        {
            var ttlExpired = cached != null && DateTime.UtcNow - cached.AnalyzedAt > ttl;
            _logger.LogInformation(
                "AiCuratedStrategy: invoking analyzer for {Url} (cached={HasCache}, ttlExpired={Expired}, guidanceChanged={GuidanceChanged})",
                context.PageUrl,
                cached != null,
                ttlExpired,
                cached != null && !guidanceMatches);

            curated = await _analyzer.AnalyzeCuratedAsync(
                context.Screenshot,
                context.Links.ToList(),
                context.PageUrl,
                requestedGuidance,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // workspace-99ve: stamp the guidance onto the result so the
            // next read can detect a guidance change and re-run.
            curated = curated with { UserGuidance = requestedGuidance };

            fromCache = false;
        }

        // workspace-hapr: diagnostic — emit the first 10 ranked keys so a
        // future user report ("AI Curated didn't reorder") can be triaged
        // from the log without re-running the model. The hash prefix is
        // unique enough to spot order-divergence between two runs.
        _logger.LogInformation(
            "AiCuratedStrategy result for {Url}: {Stories} ranked stories, {Excluded} excluded, {Sections} sections, fromCache={FromCache}. First10={First10}",
            context.PageUrl,
            curated.StoryOrderLinkKeys.Count,
            curated.ExcludedLinkKeys.Count,
            curated.Sections.Count,
            fromCache,
            string.Join(",", curated.StoryOrderLinkKeys.Take(10)));

        // workspace-hapr: surface the "AI returned no useful reordering" case
        // so the user knows whether the curated layout is actually doing
        // anything. Two failure shapes covered:
        //   (a) StoryOrderLinkKeys is empty — AI excluded everything or
        //       returned an empty ranking.
        //   (b) StoryOrderLinkKeys matches the surviving document order
        //       exactly — AI ran but the ranking equals doc order, which is
        //       indistinguishable from no curation.
        var contentLinks = context.Links.Where(l => l.Type == LinkType.Content).ToList();
        var degenerate = DetectDegenerateRanking(curated, contentLinks);

        // workspace-5oe9.13: a FRESH degenerate result gets ONE higher-effort
        // retry before we give up — minimal/low reasoning sometimes just echoes
        // document order. The retry is bounded to a single extra call.
        if (degenerate != AiCuratedDegenerateReason.None && !fromCache)
        {
            _logger.LogInformation(
                "AiCuratedStrategy: degenerate fresh result for {Url} ({Reason}) — retrying once at higher reasoning effort",
                context.PageUrl,
                degenerate);

            var retry = await _analyzer.AnalyzeCuratedAsync(
                context.Screenshot,
                context.Links.ToList(),
                context.PageUrl,
                requestedGuidance,
                reasoningEffortOverride: "medium",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            retry = retry with { UserGuidance = requestedGuidance };

            var retryDegenerate = DetectDegenerateRanking(retry, contentLinks);
            if (retryDegenerate == AiCuratedDegenerateReason.None)
            {
                curated = retry;
                degenerate = AiCuratedDegenerateReason.None;
            }
            else
            {
                degenerate = retryDegenerate;
            }
        }

        // workspace-5oe9.13: a still-degenerate FRESH result must NOT silently
        // persist as document order — flag it so the chooser asks the user.
        var needsClarification = degenerate != AiCuratedDegenerateReason.None && !fromCache;
        if (degenerate != AiCuratedDegenerateReason.None)
        {
            _logger.LogWarning(
                "AiCuratedStrategy: degenerate result for {Url} — {Reason}. Needs clarification: {NeedsClarification}.",
                context.PageUrl,
                degenerate,
                needsClarification);
        }

        // workspace-5oe9.5: turn the (snapshot) ranking into a DURABLE,
        // pattern-based config. Derive selector/url-pattern sections from the
        // AI's buckets so the layout re-applies on every revisit as article
        // URLs rotate, instead of decaying to document order.
        var allLinks = context.Links.ToList();
        var (sections, excludeSelectors, excludeUrlPatterns) =
            DeriveDurableConfig(contentLinks, curated);

        var domain = ExtractDomain(context.PageUrl);
        var baseConfig = new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = DocumentOrderStrategy.BuildUrlPattern(context.PageUrl),
            Sections = sections,
            ExcludeSelectors = excludeSelectors,
            ExcludeUrlPatterns = excludeUrlPatterns,
            CreatedAt = DateTime.UtcNow,
            ModelVersion = _config.Model,
            Kind = LayoutKind.AiCurated,
            Version = 3,
            Strategy = StrategyId,
            AiResult = curated,
            StructuralSignature = $"ai-curated:{curated.StoryOrderLinkKeys.Count}",
        };

        // SELF-TEST GATE: only persist the durable pattern if re-applying it to
        // THESE links reproduces the AI's curation (lead captured, most stories
        // kept, no excluded ad leaks). Otherwise fall back to the in-session
        // snapshot and flag the config for re-analysis so it doesn't silently
        // rot on the next visit.
        NavigationTree tree;
        SiteHierarchyConfig config;
        var selfTestPasses = sections.Count > 0
            && degenerate == AiCuratedDegenerateReason.None
            && await PatternReproducesCurationAsync(_treeBuilder, allLinks, baseConfig, curated, cancellationToken)
                .ConfigureAwait(false);

        if (selfTestPasses)
        {
            // First visit renders the SAME pattern tree every later visit will,
            // so the experience is consistent.
            tree = await _treeBuilder.BuildTreeAsync(allLinks, baseConfig, cancellationToken).ConfigureAwait(false);
            config = baseConfig;
            _logger.LogInformation(
                "AiCuratedStrategy durable config for {Url}: {Sections} sections, selectors=[{Selectors}], urlPatterns=[{UrlPatterns}], exclude=[{ExclSel}|{ExclUrl}]",
                context.PageUrl,
                sections.Count,
                string.Join(";", sections.SelectMany(s => s.ParentSelectors)),
                string.Join(";", sections.SelectMany(s => s.UrlPatterns)),
                string.Join(";", excludeSelectors),
                string.Join(";", excludeUrlPatterns));
        }
        else
        {
            tree = await _treeBuilder.BuildFromAiResultAsync(allLinks, curated, cancellationToken).ConfigureAwait(false);
            config = baseConfig with
            {
                Sections = new List<HierarchySection>(),
                ExcludeSelectors = new List<string>(),
                ExcludeUrlPatterns = new List<string>(),
                NeedsReanalyze = true,
            };
            _logger.LogWarning(
                "AiCuratedStrategy: derived pattern failed self-test for {Url} (sections={Sections}, degenerate={Degenerate}) — persisting snapshot + NeedsReanalyze",
                context.PageUrl,
                sections.Count,
                degenerate);
        }

        var summary = BuildSummary(curated, degenerate, fromCache);

        return new ScrapingStrategyResult
        {
            Tree = tree,
            Config = config,
            Summary = summary,
            NeedsClarification = needsClarification,
        };
    }

    /// <summary>
    /// workspace-5oe9.5: builds durable <see cref="HierarchySection"/>s + exclude
    /// rules from the AI's curated result. The curated schema returns story
    /// INDICES (not selectors), so identifiers are derived from each bucket's
    /// links via <see cref="SelectorDerivation"/>. A "Top Story" section is
    /// pinned from the #1 ranked link; remaining stories form the AI's named
    /// sections (when present) or a single "Stories" tier. Exclude rules are the
    /// discriminating signals unique to the excluded bucket (never shared with a
    /// kept story, so no story is accidentally dropped).
    /// </summary>
    internal static (List<HierarchySection> Sections, List<string> ExcludeSelectors, List<string> ExcludeUrlPatterns)
        DeriveDurableConfig(IReadOnlyList<LinkInfo> contentLinks, AiCuratedResult curated)
    {
        var byKey = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        foreach (var link in contentLinks)
        {
            byKey.TryAdd(AiCuratedResult.KeyFor(link.Url), link);
        }

        List<LinkInfo> Resolve(IEnumerable<string> keys) =>
            keys.Select(k => byKey.TryGetValue(k, out var l) ? l : null)
                .Where(l => l != null)
                .Cast<LinkInfo>()
                .ToList();

        var storyLinks = Resolve(curated.StoryOrderLinkKeys);
        var excludedLinks = Resolve(curated.ExcludedLinkKeys);

        var sections = new List<HierarchySection>();

        if (storyLinks.Count > 0)
        {
            var lead = storyLinks[0];
            sections.Add(new HierarchySection
            {
                Name = "Top Story",
                SortOrder = sections.Count,
                ParentSelectors = SelectorDerivation.DeriveParentSelectors(new[] { lead }),
                UrlPatterns = SelectorDerivation.DeriveUrlPatterns(new[] { lead }),
            });
        }

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        if (storyLinks.Count > 0)
        {
            assigned.Add(AiCuratedResult.KeyFor(storyLinks[0].Url));
        }

        if (curated.Sections.Count > 0)
        {
            foreach (var aiSection in curated.Sections)
            {
                var members = Resolve(aiSection.StoryLinkKeys)
                    .Where(l => assigned.Add(AiCuratedResult.KeyFor(l.Url)))
                    .ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                sections.Add(new HierarchySection
                {
                    Name = string.IsNullOrWhiteSpace(aiSection.Name) ? "Stories" : aiSection.Name,
                    SortOrder = sections.Count,
                    ParentSelectors = SelectorDerivation.DeriveParentSelectors(members),
                    UrlPatterns = SelectorDerivation.DeriveUrlPatterns(members),
                    StartCollapsed = aiSection.StartCollapsed,
                });
            }
        }

        // Any ranked stories not placed in an AI section form a single "Stories"
        // tier below the lead.
        var remaining = storyLinks.Where(l => !assigned.Contains(AiCuratedResult.KeyFor(l.Url))).ToList();
        if (remaining.Count > 0)
        {
            sections.Add(new HierarchySection
            {
                Name = "Stories",
                SortOrder = sections.Count,
                ParentSelectors = SelectorDerivation.DeriveParentSelectors(remaining),
                UrlPatterns = SelectorDerivation.DeriveUrlPatterns(remaining),
            });
        }

        var (excludeSelectors, excludeUrlPatterns) = DeriveExclusionRules(storyLinks, excludedLinks);
        return (sections, excludeSelectors, excludeUrlPatterns);
    }

    /// <summary>
    /// workspace-hapr: detects whether the AI's ranking is degenerate
    /// (empty or matches the surviving document order). The check ignores
    /// excluded links — we only compare ranking against the order links
    /// would naturally appear after exclusion.
    /// </summary>
    internal static AiCuratedDegenerateReason DetectDegenerateRanking(
        AiCuratedResult curated,
        IReadOnlyList<LinkInfo> contentLinks)
    {
        ArgumentNullException.ThrowIfNull(curated);
        ArgumentNullException.ThrowIfNull(contentLinks);

        if (curated.StoryOrderLinkKeys.Count == 0)
        {
            return AiCuratedDegenerateReason.EmptyRanking;
        }

        var excluded = new HashSet<string>(curated.ExcludedLinkKeys, StringComparer.Ordinal);
        var survivingDocOrder = contentLinks
            .Select(l => AiCuratedResult.KeyFor(l.Url))
            .Where(k => !excluded.Contains(k))
            .ToList();

        // The AI's ranking should at least produce a different sequence than
        // the surviving document order. If the prefix overlap is total, no
        // reordering happened.
        if (survivingDocOrder.Count == 0)
        {
            // Nothing left after exclusion — degenerate by construction.
            return AiCuratedDegenerateReason.EmptyRanking;
        }

        var compareLength = Math.Min(survivingDocOrder.Count, curated.StoryOrderLinkKeys.Count);
        if (compareLength != survivingDocOrder.Count
            || curated.StoryOrderLinkKeys.Count != survivingDocOrder.Count)
        {
            // The AI dropped or added links beyond the surviving set — that's
            // a non-trivial change, not a degenerate ranking.
            return AiCuratedDegenerateReason.None;
        }

        for (var i = 0; i < compareLength; i++)
        {
            if (!string.Equals(survivingDocOrder[i], curated.StoryOrderLinkKeys[i], StringComparison.Ordinal))
            {
                return AiCuratedDegenerateReason.None;
            }
        }

        return AiCuratedDegenerateReason.MatchesDocumentOrder;
    }

    /// <summary>
    /// Builds the user-visible Summary line for the strategy. Surfaces the
    /// degenerate-result reason so the chooser can warn the user that the
    /// AI didn't actually reorder (workspace-hapr).
    /// </summary>
    internal static string BuildSummary(AiCuratedResult curated, AiCuratedDegenerateReason degenerate, bool fromCache)
    {
        ArgumentNullException.ThrowIfNull(curated);

        var cachedSuffix = fromCache ? " (cached)" : string.Empty;
        var statsBody = fromCache
            ? $"{curated.StoryOrderLinkKeys.Count} stories"
            : $"{curated.StoryOrderLinkKeys.Count} stories, {curated.ExcludedLinkKeys.Count} excluded";

        return degenerate switch
        {
            AiCuratedDegenerateReason.EmptyRanking =>
                $"AI curated · empty result — AI returned no stories{cachedSuffix}",
            AiCuratedDegenerateReason.MatchesDocumentOrder =>
                $"AI curated · {statsBody} (no reordering — matches document order){cachedSuffix}",
            _ => $"AI curated · {statsBody}{cachedSuffix}",
        };
    }

    /// <summary>
    /// workspace-5oe9.5: derives exclude selectors/url-patterns that identify
    /// the excluded bucket WITHOUT overlapping any kept story's signal — so the
    /// durable exclusion never drops a real story. (Unmatched links are dropped
    /// by the section builder anyway; these rules are the belt-and-suspenders
    /// for ads that share a container with stories.)
    /// </summary>
    private static (List<string> Selectors, List<string> UrlPatterns) DeriveExclusionRules(
        IReadOnlyList<LinkInfo> storyLinks,
        IReadOnlyList<LinkInfo> excludedLinks)
    {
        var storyTokens = storyLinks
            .SelectMany(l => SelectorDerivation.DiscriminatingTokens(l.ParentSelector))
            .ToHashSet(StringComparer.Ordinal);
        var storySegments = storyLinks
            .SelectMany(l => SelectorDerivation.MeaningfulPathSegments(l.Url))
            .ToHashSet(StringComparer.Ordinal);

        var selectors = excludedLinks
            .SelectMany(l => SelectorDerivation.DiscriminatingTokens(l.ParentSelector))
            .Where(t => !storyTokens.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var urlPatterns = excludedLinks
            .SelectMany(l => SelectorDerivation.MeaningfulPathSegments(l.Url))
            .Where(s => !storySegments.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => $"/{s}/")
            .ToList();

        return (selectors, urlPatterns);
    }

    /// <summary>
    /// workspace-5oe9.5 self-test: re-applies the derived pattern config to the
    /// same links and checks it reproduces the AI's curation — the lead is
    /// kept, at least 70% of ranked stories survive, and no excluded link leaks
    /// in. Guards against over-generic or empty derived selectors before a
    /// config is trusted for future visits.
    /// </summary>
    private static async Task<bool> PatternReproducesCurationAsync(
        INavigationTreeBuilder treeBuilder,
        List<LinkInfo> allLinks,
        SiteHierarchyConfig config,
        AiCuratedResult curated,
        CancellationToken cancellationToken)
    {
        var patternTree = await treeBuilder.BuildTreeAsync(allLinks, config, cancellationToken).ConfigureAwait(false);
        var patternKeys = patternTree.GetAllNodes()
            .Where(n => !n.IsGroupHeader && n.Link.Type == LinkType.Content)
            .Select(n => AiCuratedResult.KeyFor(n.Link.Url))
            .ToHashSet(StringComparer.Ordinal);

        var excluded = new HashSet<string>(curated.ExcludedLinkKeys, StringComparer.Ordinal);
        var expectedStories = curated.StoryOrderLinkKeys.Where(k => !excluded.Contains(k)).ToList();
        if (expectedStories.Count == 0)
        {
            return false;
        }

        // No excluded ad may appear in the curated tree.
        if (patternKeys.Overlaps(excluded))
        {
            return false;
        }

        // The lead must survive.
        if (!patternKeys.Contains(expectedStories[0]))
        {
            return false;
        }

        var captured = expectedStories.Count(patternKeys.Contains);
        return captured * 10 >= expectedStories.Count * 7; // >= 70%
    }

    private static string ExtractDomain(string pageUrl) => HierarchyDomainKey.FromUrl(pageUrl);

    /// <summary>
    /// workspace-99ve: normalises user guidance for both the analyzer call
    /// and the cache-match comparison. Trim+collapse whitespace so trailing
    /// newlines or extra spaces don't cause spurious cache misses; empty
    /// string maps to null so cached results without guidance still match
    /// new runs without guidance.
    /// </summary>
    private static string? NormalizeGuidance(string? guidance)
    {
        if (string.IsNullOrWhiteSpace(guidance))
        {
            return null;
        }

        var trimmed = guidance.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
