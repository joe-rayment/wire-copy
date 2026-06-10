// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser.ScrapingStrategies;

/// <summary>
/// RSS Feed scraping strategy. Available when <see cref="IRssFeedDetector"/>
/// finds an advertised feed in the HTML or via well-known probe paths.
/// Selecting it replaces the link list with feed items.
/// </summary>
public sealed class RssFeedStrategy : IScrapingStrategy
{
    public const string StrategyId = "RssFeed";

    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IRssFeedDetector _feedDetector;
    private readonly ILogger<RssFeedStrategy> _logger;

    public RssFeedStrategy(
        INavigationTreeBuilder treeBuilder,
        IRssFeedDetector feedDetector,
        ILogger<RssFeedStrategy> logger)
    {
        _treeBuilder = treeBuilder;
        _feedDetector = feedDetector;
        _logger = logger;
    }

    public string Id => StrategyId;

    public string DisplayName => "RSS Feed";

    public string Description => "Replace links with the site's RSS feed";

    public async Task<ScrapingStrategyAvailability> IsAvailableAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default)
    {
        // Honour saved feed URL first to avoid probing every page load.
        if (context.SavedConfig?.RssFeedUrl is { Length: > 0 } savedFeed)
        {
            return new ScrapingStrategyAvailability
            {
                IsAvailable = true,
                StatusDetail = $"feed · {savedFeed}",
            };
        }

        var feedUrl = await ResolveFeedUrlAsync(context, cancellationToken).ConfigureAwait(false);
        if (feedUrl == null)
        {
            return new ScrapingStrategyAvailability
            {
                IsAvailable = false,
                ReasonWhenUnavailable = "No RSS/Atom feed found",
            };
        }

        return new ScrapingStrategyAvailability
        {
            IsAvailable = true,
            StatusDetail = $"feed · {feedUrl}",
        };
    }

    public async Task<ScrapingStrategyResult> BuildTreeAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default)
    {
        var feedUrl = context.SavedConfig?.RssFeedUrl;
        if (string.IsNullOrEmpty(feedUrl))
        {
            feedUrl = await ResolveFeedUrlAsync(context, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(feedUrl))
        {
            throw new InvalidOperationException("RssFeedStrategy: no feed available for " + context.PageUrl);
        }

        var feedItems = await _feedDetector.ParseFeedAsync(feedUrl, cancellationToken).ConfigureAwait(false);
        if (feedItems.Count == 0)
        {
            throw new InvalidOperationException(
                $"RssFeedStrategy: feed at {feedUrl} returned no items");
        }

        var tree = await _treeBuilder.BuildTreeAsync(feedItems, cancellationToken).ConfigureAwait(false);

        var domain = ExtractDomain(context.PageUrl);
        var config = new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = DocumentOrderStrategy.BuildUrlPattern(context.PageUrl),
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "rss-feed",
            Kind = LayoutKind.RssFeed,
            Version = 2,
            Strategy = StrategyId,
            RssFeedUrl = feedUrl,
            StructuralSignature = $"rss:{feedItems.Count}",
        };

        _logger.LogInformation(
            "RssFeedStrategy built tree for {Url} from feed {Feed}: {Count} items",
            context.PageUrl,
            feedUrl,
            feedItems.Count);

        return new ScrapingStrategyResult
        {
            Tree = tree,
            Config = config,
            Summary = $"RSS feed · {feedItems.Count} articles",
        };
    }

    private static string ExtractDomain(string pageUrl) => HierarchyDomainKey.FromUrl(pageUrl);

    private async Task<string?> ResolveFeedUrlAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken)
    {
        var advertised = _feedDetector.DetectFeeds(context.Html ?? string.Empty, context.PageUrl);
        if (advertised.Count > 0)
        {
            return advertised[0].Url;
        }

        var probed = await _feedDetector.ProbeWellKnownFeedsAsync(
            context.PageUrl, cancellationToken).ConfigureAwait(false);
        return probed.Count > 0 ? probed[0].Url : null;
    }
}
