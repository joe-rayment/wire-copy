// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// Describes an RSS or Atom feed discovered on a web page
/// via &lt;link rel="alternate" type="application/rss+xml"&gt; or similar.
/// </summary>
public sealed record FeedInfo
{
    /// <summary>
    /// Absolute URL of the feed.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Human-readable title from the link element's title attribute, if present.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Feed format detected from the type attribute.
    /// </summary>
    public required FeedType Type { get; init; }
}

/// <summary>
/// Supported feed formats.
/// </summary>
public enum FeedType
{
    Rss,
    Atom,
}
