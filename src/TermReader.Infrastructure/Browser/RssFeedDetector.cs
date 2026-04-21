// Educational and personal use only.

using System.ServiceModel.Syndication;
using System.Xml;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Detects RSS/Atom feed links by parsing &lt;link rel="alternate"&gt; elements in HTML,
/// and parses feed content into LinkInfo objects for display.
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
    private readonly HttpClient _httpClient;

    public RssFeedDetector(ILogger<RssFeedDetector> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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

    /// <inheritdoc />
    public async Task<List<LinkInfo>> ParseFeedAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        var items = new List<LinkInfo>();

        try
        {
            _logger.LogInformation("Fetching RSS feed: {Url}", feedUrl);

            using var response = await _httpClient.GetAsync(feedUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var xmlReader = XmlReader.Create(stream);

            var feed = SyndicationFeed.Load(xmlReader);
            if (feed == null)
            {
                _logger.LogWarning("Feed at {Url} returned null", feedUrl);
                return items;
            }

            // Sort by publish date (newest first), then take first MaxContentLinks.
            // PublishDate getter can throw XmlException for malformed dates, so we
            // use a safe accessor that falls back to DateTimeOffset.MinValue.
            var sortedItems = feed.Items
                .OrderByDescending(i => GetPublishDateSafe(i))
                .Take(NavigationTreeBuilder.MaxContentLinks);

            foreach (var synItem in sortedItems)
            {
                // RSS 2.0 <link> and Atom <link href="..."> both populate Links collection,
                // but some feeds only set the Id. Try Links first, fall back to Id.
                var linkUri = synItem.Links.FirstOrDefault()?.Uri;
                var link = linkUri?.IsAbsoluteUri == true
                    ? linkUri.AbsoluteUri
                    : linkUri?.ToString();

                if (string.IsNullOrEmpty(link))
                {
                    // RSS 2.0 <guid> or <link> may populate Id instead
                    link = synItem.Id;
                }

                if (string.IsNullOrEmpty(link) || !Uri.TryCreate(link, UriKind.Absolute, out _))
                {
                    continue;
                }

                var displayText = synItem.Title?.Text ?? link;
                if (string.IsNullOrWhiteSpace(displayText))
                {
                    continue;
                }

                var author = synItem.Authors.FirstOrDefault()?.Name
                    ?? synItem.Authors.FirstOrDefault()?.Email;

                var pubDate = GetPublishDateSafe(synItem);
                items.Add(new LinkInfo
                {
                    Url = link,
                    DisplayText = displayText,
                    Type = LinkType.Content,
                    ImportanceScore = 50,
                    Author = author,
                    PublishedDate = pubDate != DateTimeOffset.MinValue
                        ? pubDate.UtcDateTime
                        : null,
                });
            }

            _logger.LogInformation(
                "Parsed {Count} items from feed {Url} (feed title: {Title})",
                items.Count,
                feedUrl,
                feed.Title?.Text ?? "(untitled)");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Feed fetch cancelled: {Url}", feedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/parse feed: {Url}", feedUrl);
        }

        return items;
    }

    /// <summary>
    /// Safely reads PublishDate, returning MinValue if the date format is invalid.
    /// SyndicationItem.PublishDate throws XmlException for malformed dates.
    /// </summary>
    private static DateTimeOffset GetPublishDateSafe(SyndicationItem item)
    {
        try
        {
            return item.PublishDate;
        }
        catch (XmlException)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
