// Licensed under the MIT License. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Generates layout candidates (document-order, AI hierarchical, RSS) for a page.
/// Document-order is always instant. AI and RSS run in parallel when available.
/// </summary>
public class LayoutCandidateGenerator : ILayoutCandidateGenerator
{
    private readonly INavigationTreeBuilder _treeBuilder;
    private readonly IRssFeedDetector _feedDetector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LayoutCandidateGenerator> _logger;

    public LayoutCandidateGenerator(
        INavigationTreeBuilder treeBuilder,
        IRssFeedDetector feedDetector,
        IServiceScopeFactory scopeFactory,
        ILogger<LayoutCandidateGenerator> logger)
    {
        _treeBuilder = treeBuilder;
        _feedDetector = feedDetector;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<LayoutCandidate>> GenerateCandidatesAsync(
        List<LinkInfo> links,
        string html,
        string pageUrl,
        byte[]? screenshot,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<LayoutCandidate>();

        // 1. Document-order layout (always available, instant)
        var docOrderTree = await _treeBuilder.BuildTreeAsync(links, cancellationToken).ConfigureAwait(false);
        var docOrderConfig = BuildDocumentOrderConfig(pageUrl, docOrderTree);
        candidates.Add(new LayoutCandidate
        {
            Config = docOrderConfig,
            Summary = $"Document Order · {CountContentLinks(links)} links",
            PreviewTree = docOrderTree,
        });

        // 2. AI hierarchical and 3. RSS feed — run in parallel
        var aiTask = TryGenerateAiCandidateAsync(links, pageUrl, screenshot, cancellationToken);
        var rssTask = TryGenerateRssCandidateAsync(html, pageUrl, cancellationToken);

        await Task.WhenAll(aiTask, rssTask).ConfigureAwait(false);

        var aiCandidate = await aiTask.ConfigureAwait(false);
        if (aiCandidate != null)
        {
            candidates.Add(aiCandidate);
        }

        var rssCandidate = await rssTask.ConfigureAwait(false);
        if (rssCandidate != null)
        {
            candidates.Add(rssCandidate);
        }

        // De-duplicate by structural signature
        candidates = DeduplicateCandidates(candidates);

        // If we only have the document-order layout and AI is available,
        // try a creative alternative grouping. The AI gets a nudge to try
        // something unconventional — a "wild shot" that might surface an
        // interesting way to organize the content.
        if (candidates.Count <= 1 && screenshot != null && screenshot.Length > 0)
        {
            var altCandidate = await TryGenerateCreativeAlternativeAsync(
                links, pageUrl, screenshot, aiCandidate, cancellationToken).ConfigureAwait(false);
            if (altCandidate != null)
            {
                candidates.Add(altCandidate);
            }
        }

        _logger.LogInformation(
            "Generated {Count} layout candidate(s) for {Url}",
            candidates.Count,
            pageUrl);

        return candidates;
    }

    /// <summary>
    /// Computes a structural signature for de-duplication.
    /// Two configs with the same section names and link counts are considered identical.
    /// </summary>
    internal static string ComputeSignature(SiteHierarchyConfig config)
    {
        var sb = new StringBuilder();
        foreach (var section in config.Sections.OrderBy(s => s.SortOrder))
        {
            sb.Append(section.Name);
            sb.Append(':');
            sb.Append(section.ParentSelectors.Count + section.UrlPatterns.Count);
            sb.Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    private static SiteHierarchyConfig BuildDocumentOrderConfig(string pageUrl, NavigationTree tree)
    {
        string domain;
        try
        {
            domain = new Uri(pageUrl).Host.ToLowerInvariant();
        }
        catch
        {
            domain = "unknown";
        }

        return new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = BuildUrlPattern(pageUrl),
            Sections = [],
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "document-order",
            Kind = LayoutKind.DocumentOrder,
            StructuralSignature = $"doc-order:{tree.TotalLinks}",
        };
    }

    private static string BuildUrlPattern(string pageUrl)
    {
        try
        {
            var uri = new Uri(pageUrl);
            var escapedDomain = System.Text.RegularExpressions.Regex.Escape(uri.Host);
            var pathPattern = uri.AbsolutePath == "/"
                ? "/?"
                : System.Text.RegularExpressions.Regex.Escape(uri.AbsolutePath);
            return $"^https?://(www\\.)?{escapedDomain}{pathPattern}";
        }
        catch
        {
            return ".*";
        }
    }

    private static List<LayoutCandidate> DeduplicateCandidates(List<LayoutCandidate> candidates)
    {
        var seen = new HashSet<string>();
        var unique = new List<LayoutCandidate>();

        foreach (var candidate in candidates)
        {
            var sig = candidate.Config.StructuralSignature ?? string.Empty;
            if (seen.Add(sig))
            {
                unique.Add(candidate);
            }
            else
            {
                // Keep document-order if it's the duplicate (it's the fallback)
                // Skip AI/RSS duplicates
            }
        }

        return unique;
    }

    private static int CountContentLinks(List<LinkInfo> links)
    {
        var count = 0;
        foreach (var link in links)
        {
            if (link.Type == LinkType.Content)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<LayoutCandidate?> TryGenerateAiCandidateAsync(
        List<LinkInfo> links,
        string pageUrl,
        byte[]? screenshot,
        CancellationToken cancellationToken)
    {
        if (screenshot == null || screenshot.Length == 0)
        {
            return null;
        }

        var contentLinkCount = CountContentLinks(links);
        if (contentLinkCount < 3)
        {
            return null;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var analyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();
            if (analyzer == null || !analyzer.IsConfigured)
            {
                return null;
            }

            var config = await analyzer.AnalyzePageHierarchyAsync(
                screenshot, links, pageUrl, promptSuffix: null, cancellationToken).ConfigureAwait(false);

            config = config with
            {
                Kind = LayoutKind.AiHierarchical,
                StructuralSignature = ComputeSignature(config),
            };

            var tree = await _treeBuilder.BuildTreeAsync(links, config, cancellationToken).ConfigureAwait(false);

            return new LayoutCandidate
            {
                Config = config,
                Summary = $"AI Layout · {config.Sections.Count} sections",
                PreviewTree = tree,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI layout candidate generation failed for {Url}", pageUrl);
            return null;
        }
    }

    /// <summary>
    /// When the standard AI layout was identical to document-order (or unavailable),
    /// asks the AI for a deliberately different grouping — a creative "wild shot."
    /// </summary>
    private async Task<LayoutCandidate?> TryGenerateCreativeAlternativeAsync(
        List<LinkInfo> links,
        string pageUrl,
        byte[] screenshot,
        LayoutCandidate? firstAiResult,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var analyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();
            if (analyzer == null || !analyzer.IsConfigured)
            {
                return null;
            }

            var creativeSuffix = "IMPORTANT: Your previous grouping was too similar to the default. " +
                "Try a COMPLETELY DIFFERENT approach. Some ideas:\n" +
                "- Group by topic/theme instead of page position (e.g., all politics together, all tech together)\n" +
                "- Group by content type (opinion pieces vs news reports vs features vs reviews)\n" +
                "- Group by recency (breaking/today vs this week vs older)\n" +
                "- Group by format (long reads vs quick takes vs multimedia)\n" +
                "Pick whichever approach produces the most useful grouping for a reader.";

            _logger.LogInformation("Trying creative alternative layout for {Url}", pageUrl);

            var config = await analyzer.AnalyzePageHierarchyAsync(
                screenshot, links, pageUrl, creativeSuffix, cancellationToken).ConfigureAwait(false);

            config = config with
            {
                Kind = LayoutKind.AiHierarchical,
                StructuralSignature = ComputeSignature(config) + "-alt",
                ModelVersion = config.ModelVersion + "-creative",
            };

            // Check it's actually different from the first AI result
            if (firstAiResult != null &&
                config.Sections.Count == firstAiResult.Config.Sections.Count &&
                config.Sections.All(s =>
                    firstAiResult.Config.Sections.Any(fs =>
                        string.Equals(fs.Name, s.Name, StringComparison.OrdinalIgnoreCase))))
            {
                _logger.LogDebug("Creative alternative was identical to first AI result, skipping");
                return null;
            }

            var tree = await _treeBuilder.BuildTreeAsync(links, config, cancellationToken).ConfigureAwait(false);

            return new LayoutCandidate
            {
                Config = config,
                Summary = $"AI Alternative \u00b7 {config.Sections.Count} sections",
                PreviewTree = tree,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Creative alternative generation failed for {Url}", pageUrl);
            return null;
        }
    }

    private async Task<LayoutCandidate?> TryGenerateRssCandidateAsync(
        string html,
        string pageUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var feeds = _feedDetector.DetectFeeds(html, pageUrl);

        // If <link> tag detection found nothing, probe well-known feed URLs
        if (feeds == null || feeds.Count == 0)
        {
            feeds = await _feedDetector.ProbeWellKnownFeedsAsync(pageUrl, cancellationToken).ConfigureAwait(false);
        }

        if (feeds == null || feeds.Count == 0)
        {
            return null;
        }

        // Use the first feed (typically the main one)
        var feedInfo = feeds[0];

        try
        {
            var feedItems = await _feedDetector.ParseFeedAsync(feedInfo.Url, cancellationToken).ConfigureAwait(false);
            if (feedItems.Count == 0)
            {
                return null;
            }

            var tree = await _treeBuilder.BuildTreeAsync(feedItems, cancellationToken).ConfigureAwait(false);

            var domain = new Uri(pageUrl).Host.ToLowerInvariant();
            var config = new SiteHierarchyConfig
            {
                Domain = domain,
                UrlPattern = BuildUrlPattern(pageUrl),
                Sections = [],
                CreatedAt = DateTime.UtcNow,
                ModelVersion = "rss-feed",
                Kind = LayoutKind.RssFeed,
                RssFeedUrl = feedInfo.Url,
                StructuralSignature = $"rss:{feedItems.Count}",
            };

            return new LayoutCandidate
            {
                Config = config,
                Summary = $"RSS Feed · {feedItems.Count} articles",
                PreviewTree = tree,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RSS layout candidate generation failed for {Url}", pageUrl);
            return null;
        }
    }
}
