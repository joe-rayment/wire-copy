// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// Structured signals extracted from a single HTML parse for page classification.
/// Replaces the boolean isArticlePage with multi-dimensional data that a
/// scoring classifier can weigh additively.
/// </summary>
public sealed record PageSignals
{
    /// <summary>
    /// Value of og:type meta tag (e.g., "article", "website"). Null if absent.
    /// </summary>
    public string? OgType { get; init; }

    /// <summary>
    /// First @type value from ld+json script (e.g., "NewsArticle", "WebSite"). Null if absent.
    /// </summary>
    public string? LdJsonType { get; init; }

    /// <summary>
    /// Count of &lt;article&gt; HTML elements.
    /// 1 = single article body; 3+ = article cards on a listing page.
    /// </summary>
    public int ArticleContainerCount { get; init; }

    /// <summary>
    /// Count of elements with role="article" attribute.
    /// High count (3+) indicates article cards on a listing page, not a single article.
    /// </summary>
    public int RoleArticleCount { get; init; }

    /// <summary>
    /// Whether any element has an article-body class (article-body, entry-content,
    /// post-content, wp-block-post-content, etc.).
    /// </summary>
    public bool HasArticleBodyClass { get; init; }

    /// <summary>
    /// Whether an &lt;h1&gt; element is present (article headline signal).
    /// </summary>
    public bool HasH1 { get; init; }

    /// <summary>
    /// Count of paragraphs with more than 200 characters of text,
    /// excluding boilerplate regions (nav, footer, aside).
    /// </summary>
    public int DeepParagraphCount { get; init; }

    /// <summary>
    /// Count of &lt;time&gt; elements. High count (20+) suggests a listing page
    /// with many dated items.
    /// </summary>
    public int TimeElementCount { get; init; }

    /// <summary>
    /// Whether a &lt;main&gt; element or role="main" is present.
    /// </summary>
    public bool HasMainElement { get; init; }
}
