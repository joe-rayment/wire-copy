// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.ScrapingStrategies;

/// <summary>
/// Default scraping strategy: render the link list in the HTML order
/// the page returned, with the existing cheap ad/sponsor pre-filter
/// (already applied by <see cref="LinkExtractor"/>).
/// Always available; never calls external services.
/// </summary>
public sealed class DocumentOrderStrategy : IScrapingStrategy
{
    public const string StrategyId = "DocumentOrder";

    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly ILogger<DocumentOrderStrategy> _logger;

    public DocumentOrderStrategy(
        INavigationTreeBuilder treeBuilder,
        ILogger<DocumentOrderStrategy> logger)
    {
        _treeBuilder = treeBuilder;
        _logger = logger;
    }

    public string Id => StrategyId;

    public string DisplayName => "Document Order";

    public string Description => "HTML order with basic ad filter (default)";

    public Task<ScrapingStrategyAvailability> IsAvailableAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ScrapingStrategyAvailability { IsAvailable = true });
    }

    public async Task<ScrapingStrategyResult> BuildTreeAsync(
        ScrapingStrategyContext context,
        CancellationToken cancellationToken = default)
    {
        var tree = await _treeBuilder.BuildTreeAsync(
            context.Links.ToList(), cancellationToken).ConfigureAwait(false);

        var domain = ExtractDomain(context.PageUrl);
        var config = new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = BuildUrlPattern(context.PageUrl),
            Sections = new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "document-order",
            Kind = LayoutKind.DocumentOrder,
            Version = 2,
            Strategy = StrategyId,
            StructuralSignature = $"doc-order:{tree.TotalLinks}",
        };

        _logger.LogInformation(
            "DocumentOrderStrategy built tree for {Url}: {Count} links",
            context.PageUrl,
            tree.TotalLinks);

        return new ScrapingStrategyResult
        {
            Tree = tree,
            Config = config,
            Summary = $"Document order · {tree.TotalLinks} links",
        };
    }

    internal static string BuildUrlPattern(string pageUrl)
    {
        try
        {
            var uri = new Uri(pageUrl);
            var escapedDomain = Regex.Escape(uri.Host);
            var pathPattern = uri.AbsolutePath == "/" || string.IsNullOrEmpty(uri.AbsolutePath)
                ? "/?"
                : Regex.Escape(uri.AbsolutePath);
            return $"^https?://(www\\.)?{escapedDomain}{pathPattern}";
        }
        catch
        {
            return ".*";
        }
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
