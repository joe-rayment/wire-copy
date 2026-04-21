// Educational and personal use only.

using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Detects RSS/Atom feed links by parsing &lt;link rel="alternate"&gt; elements in HTML.
/// </summary>
public class RssFeedDetector : IRssFeedDetector
{
    private static readonly HashSet<string> RssTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/rss+xml",
        "application/rdf+xml",
    };

    private static readonly HashSet<string> AtomTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/atom+xml",
    };

    private readonly ILogger<RssFeedDetector> _logger;

    public RssFeedDetector(ILogger<RssFeedDetector> logger)
    {
        _logger = logger;
    }

    public List<FeedInfo> DetectFeeds(string html, string pageUrl)
    {
        var feeds = new List<FeedInfo>();

        if (string.IsNullOrEmpty(html))
        {
            return feeds;
        }

        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Look for <link rel="alternate" type="application/rss+xml|atom+xml" href="...">
            var linkNodes = doc.DocumentNode.SelectNodes("//link[@rel and @type and @href]");
            if (linkNodes == null)
            {
                return feeds;
            }

            Uri? baseUri = null;
            try
            {
                baseUri = new Uri(pageUrl);
            }
            catch
            {
                _logger.LogDebug("Could not parse base URL for feed detection: {Url}", pageUrl);
                return feeds;
            }

            foreach (var node in linkNodes)
            {
                var rel = node.GetAttributeValue("rel", string.Empty);
                if (!rel.Contains("alternate", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = node.GetAttributeValue("type", string.Empty);
                var href = node.GetAttributeValue("href", string.Empty);

                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                FeedType? feedType = null;
                if (RssTypes.Contains(type))
                {
                    feedType = FeedType.Rss;
                }
                else if (AtomTypes.Contains(type))
                {
                    feedType = FeedType.Atom;
                }

                if (feedType == null)
                {
                    continue;
                }

                // Resolve relative URLs
                string absoluteUrl;
                try
                {
                    absoluteUrl = new Uri(baseUri, href).AbsoluteUri;
                }
                catch
                {
                    _logger.LogDebug("Could not resolve feed URL: {Href} on {Base}", href, pageUrl);
                    continue;
                }

                var title = node.GetAttributeValue("title", null!);

                feeds.Add(new FeedInfo
                {
                    Url = absoluteUrl,
                    Title = title,
                    Type = feedType.Value,
                });
            }

            if (feeds.Count > 0)
            {
                _logger.LogInformation(
                    "Detected {Count} feed(s) on {Url}: {Feeds}",
                    feeds.Count,
                    pageUrl,
                    string.Join(", ", feeds.Select(f => $"{f.Type}:{f.Url}")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Feed detection failed for {Url}", pageUrl);
        }

        return feeds;
    }
}
