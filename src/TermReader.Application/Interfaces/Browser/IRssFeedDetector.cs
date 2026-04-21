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
}
