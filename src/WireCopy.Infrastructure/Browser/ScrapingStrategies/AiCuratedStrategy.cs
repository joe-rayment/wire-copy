// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces.Browser;
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
                cancellationToken).ConfigureAwait(false);

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
        if (degenerate != AiCuratedDegenerateReason.None)
        {
            _logger.LogWarning(
                "AiCuratedStrategy: degenerate result for {Url} — {Reason}. User-visible layout will match document order.",
                context.PageUrl,
                degenerate);
        }

        var tree = await _treeBuilder.BuildFromAiResultAsync(
            context.Links.ToList(), curated, cancellationToken).ConfigureAwait(false);

        var domain = ExtractDomain(context.PageUrl);
        var config = new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = DocumentOrderStrategy.BuildUrlPattern(context.PageUrl),
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = _config.Model,
            Kind = LayoutKind.AiCurated,
            Version = 2,
            Strategy = StrategyId,
            AiResult = curated,
            StructuralSignature = $"ai-curated:{curated.StoryOrderLinkKeys.Count}",
        };

        var summary = BuildSummary(curated, degenerate, fromCache);

        return new ScrapingStrategyResult
        {
            Tree = tree,
            Config = config,
            Summary = summary,
        };
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

    private static string ExtractDomain(string pageUrl)
    {
        try
        {
            return new Uri(pageUrl).Host.ToLowerInvariant();
        }
        catch
        {
            return "unknown";
        }
    }

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
