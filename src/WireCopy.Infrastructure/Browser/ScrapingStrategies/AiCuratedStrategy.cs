// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser.ScrapingStrategies;

/// <summary>
/// AI Curated scraping strategy. Sends the page links + screenshot to
/// Anthropic and asks for an explicit excluded/stories split. Excluded
/// links are deleted from the tree (not pushed down).
/// Result is cached on <see cref="SiteHierarchyConfig.AiResult"/> with a TTL.
/// </summary>
public sealed class AiCuratedStrategy : IScrapingStrategy
{
    public const string StrategyId = "AiCurated";

    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IHierarchyAnalyzer _analyzer;
    private readonly AnthropicConfiguration _config;
    private readonly ILogger<AiCuratedStrategy> _logger;

    public AiCuratedStrategy(
        INavigationTreeBuilder treeBuilder,
        IHierarchyAnalyzer analyzer,
        IOptions<AnthropicConfiguration> config,
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
                ReasonWhenUnavailable = "No Anthropic API key (use :set anthropic-key)",
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

        if (cached != null && DateTime.UtcNow - cached.AnalyzedAt <= ttl)
        {
            curated = cached;
            fromCache = true;
            _logger.LogInformation(
                "AiCuratedStrategy: using cached result for {Url} ({Age} old)",
                context.PageUrl,
                DateTime.UtcNow - cached.AnalyzedAt);
        }
        else
        {
            _logger.LogInformation(
                "AiCuratedStrategy: invoking analyzer for {Url} (cached={HasCache}, ttlExpired={Expired})",
                context.PageUrl,
                cached != null,
                cached != null);

            curated = await _analyzer.AnalyzeCuratedAsync(
                context.Screenshot,
                context.Links.ToList(),
                context.PageUrl,
                cancellationToken).ConfigureAwait(false);

            fromCache = false;
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

        var summary = fromCache
            ? $"AI curated · {curated.StoryOrderLinkKeys.Count} stories (cached)"
            : $"AI curated · {curated.StoryOrderLinkKeys.Count} stories, {curated.ExcludedLinkKeys.Count} excluded";

        return new ScrapingStrategyResult
        {
            Tree = tree,
            Config = config,
            Summary = summary,
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
}
