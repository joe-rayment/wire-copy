// Educational and personal use only.

using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Detects RSS/Atom feed links in page HTML and parses feed content.
/// </summary>
public interface IRssFeedDetector
{
    /// <summary>
    /// Scans HTML for &lt;link rel="alternate"&gt; elements that point to RSS or Atom feeds.
    /// </summary>
    /// <param name="html">Raw HTML of the page.</param>
    /// <param name="pageUrl">Base URL for resolving relative feed URLs.</param>
    /// <returns>List of discovered feeds (may be empty).</returns>
    List<FeedInfo> DetectFeeds(string html, string pageUrl);

    /// <summary>
    /// Fetches and parses an RSS or Atom feed, returning items as LinkInfo objects.
    /// Items are sorted chronologically (newest first) and capped at 100.
    /// This is a lazy operation — only called when the user requests the RSS layout.
    /// </summary>
    /// <param name="feedUrl">Absolute URL of the feed to parse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of feed items as LinkInfo, or empty on failure.</returns>
    Task<List<LinkInfo>> ParseFeedAsync(string feedUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes well-known feed URL paths (/feed/, /rss, /feed.xml, etc.) when
    /// &lt;link&gt; tag detection finds nothing. Many sites serve feeds at standard
    /// paths without advertising them in HTML.
    /// </summary>
    /// <param name="pageUrl">URL of the page to probe from (uses origin).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered feeds, or empty.</returns>
    Task<List<FeedInfo>> ProbeWellKnownFeedsAsync(string pageUrl, CancellationToken cancellationToken = default);
}
