// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Domain.Entities.Browser;

/// <summary>
/// Represents a loaded web page with all associated data.
/// Aggregate root for browser domain.
/// </summary>
public class Page
{
    /// <summary>
    /// Unique identifier for this page instance.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// URL of the page.
    /// </summary>
    public string Url { get; private set; }

    /// <summary>
    /// Page metadata (title, description, etc.).
    /// </summary>
    public PageMetadata Metadata { get; private set; }

    /// <summary>
    /// Raw HTML source of the page.
    /// </summary>
    public string RawHtml { get; private set; }

    /// <summary>
    /// Hierarchical link tree extracted from the page.
    /// </summary>
    public NavigationTree? LinkTree { get; private set; }

    /// <summary>
    /// Clean readable content (only available for article pages).
    /// </summary>
    public ReadableContent? ReadableContent { get; private set; }

    /// <summary>
    /// Classification of this page (Article, LinkList, or Unknown).
    /// </summary>
    public PageClassification Classification { get; private set; } = PageClassification.Unknown;

    /// <summary>
    /// RSS/Atom feeds discovered on this page via &lt;link rel="alternate"&gt;.
    /// Empty if no feeds found.
    /// </summary>
    public IReadOnlyList<FeedInfo> DetectedFeeds { get; private set; } = Array.Empty<FeedInfo>();

    /// <summary>
    /// Timestamp when the page was loaded.
    /// </summary>
    public DateTime LoadedAt { get; private set; }

    private Page(string url, string rawHtml, PageMetadata metadata)
    {
        Id = Guid.NewGuid();
        Url = url;
        RawHtml = rawHtml;
        Metadata = metadata;
        LoadedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new Page instance.
    /// </summary>
    public static Page Create(string url, string rawHtml, PageMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be empty", nameof(url));

        if (metadata == null)
            throw new ArgumentNullException(nameof(metadata));

        return new Page(url, rawHtml ?? string.Empty, metadata);
    }

    /// <summary>
    /// Sets the navigation tree for this page.
    /// </summary>
    public void SetLinkTree(NavigationTree tree)
    {
        LinkTree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    /// <summary>
    /// Sets the readable content for this page.
    /// </summary>
    public void SetReadableContent(ReadableContent content)
    {
        ReadableContent = content ?? throw new ArgumentNullException(nameof(content));
    }

    /// <summary>
    /// Sets the page classification.
    /// </summary>
    public void SetClassification(PageClassification classification)
    {
        Classification = classification;
    }

    /// <summary>
    /// Sets discovered RSS/Atom feeds for this page.
    /// </summary>
    public void SetDetectedFeeds(IReadOnlyList<FeedInfo> feeds)
    {
        DetectedFeeds = feeds ?? Array.Empty<FeedInfo>();
    }

    /// <summary>
    /// Checks if this page has readable content available.
    /// </summary>
    public bool HasReadableContent() => ReadableContent != null;

    /// <summary>
    /// Checks if this page has links available.
    /// </summary>
    public bool HasLinks() => LinkTree != null && LinkTree.TotalLinks > 0;

    /// <summary>
    /// Gets a summary string for debugging/logging.
    /// </summary>
    public string GetSummary()
    {
        var linkCount = LinkTree?.TotalLinks ?? 0;
        var hasReadable = ReadableContent != null ? "Yes" : "No";

        return $"Page: {Metadata.Title} | URL: {Url} | Links: {linkCount} | Readable: {hasReadable} | Type: {Classification}";
    }
}
