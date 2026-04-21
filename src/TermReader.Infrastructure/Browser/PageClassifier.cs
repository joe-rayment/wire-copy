// Educational and personal use only.

using System.Text.RegularExpressions;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Classifies pages as Article, LinkList, or Unknown.
/// Uses URL-first logic: URL shape is checked before HTML-based article
/// heuristics, because URL patterns are stable across all sites while
/// HTML structures vary wildly (e.g. &lt;article&gt; cards on homepages).
/// </summary>
internal static partial class PageClassifier
{
    /// <summary>
    /// Bump this when classification logic changes to invalidate stale build caches.
    /// </summary>
    public const int ClassificationVersion = 4;

    // Scoring thresholds
    private const int ArticleThreshold = 25;
    private const int LinkListThreshold = -25;

    /// <summary>
    /// Classifies a page using additive signal scoring. Each HTML/URL signal
    /// contributes weighted points; the total score determines classification.
    /// No single signal is decisive — this prevents the "fix one site, break another" cycle.
    /// </summary>
    /// <param name="signals">Structured signals extracted from HTML by PageSignalExtractor.</param>
    /// <param name="contentLinkCount">Count of content-type links from link extraction.</param>
    /// <param name="url">Final page URL (post-redirect).</param>
    /// <returns>Classification and the raw score for debugging.</returns>
    public static (PageClassification Classification, int Score) ClassifyScored(
        PageSignals signals,
        int contentLinkCount,
        string url)
    {
        var score = 0;

        // === ARTICLE signals (positive) ===

        // og:type = "article" — standardized, reliable across most CMS platforms
        if (string.Equals(signals.OgType, "article", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        // ld+json @type contains Article/NewsArticle/BlogPosting — authoritative schema.org data
        if (signals.LdJsonType != null && IsArticleLdJsonType(signals.LdJsonType))
        {
            score += 30;
        }

        // Single <article> container — semantic article wrapper
        if (signals.ArticleContainerCount == 1)
        {
            score += 15;
        }

        // article-body / entry-content class — CMS-specific article body marker
        if (signals.HasArticleBodyClass)
        {
            score += 15;
        }

        // <h1> present — article headline
        if (signals.HasH1)
        {
            score += 10;
        }

        // Deep paragraphs (>200 chars) — substantial article content
        // Scaled: 3+ paragraphs is a moderate signal, 10+ is strong (Wikipedia, longform)
        if (signals.DeepParagraphCount >= 10)
        {
            score += 25;
        }
        else if (signals.DeepParagraphCount >= 3)
        {
            score += 15;
        }

        // <main> or role="main" with context — structural main content
        if (signals.HasMainElement)
        {
            score += 10;
        }

        // Date-slug URL (/YYYY/MM/DD/) — strong article URL pattern
        if (IsArticleUrlPattern(url))
        {
            score += 10;
        }

        // === LINKLIST signals (negative) ===

        // Root URL (/) — always a homepage
        if (IsRootUrl(url))
        {
            score -= 50;
        }

        // og:type = "website" — publisher homepage/index
        if (string.Equals(signals.OgType, "website", StringComparison.OrdinalIgnoreCase))
        {
            score -= 25;
        }

        // ld+json @type = WebSite/CollectionPage — authoritative index signal
        if (signals.LdJsonType != null && IsIndexLdJsonType(signals.LdJsonType))
        {
            score -= 25;
        }

        // Multiple <article> containers (3+) — article cards on listing page
        if (signals.ArticleContainerCount >= 3)
        {
            score -= 25;
        }

        // Multiple role="article" elements (3+) — card-based listing page
        if (signals.RoleArticleCount >= 3)
        {
            score -= 20;
        }

        // Bare section URL (/science but NOT /science/id/slug)
        if (IsSectionUrlPattern(url))
        {
            score -= 30;
        }

        // Content link count: scaled penalty, -5 per 15 links above 10, capped at -20
        // Modest penalty — articles like Wikipedia can have 200+ inline links
        if (contentLinkCount > 10)
        {
            var linkPenalty = Math.Min(20, ((contentLinkCount - 10) / 15 + 1) * 5);
            score -= linkPenalty;
        }

        // Many <time> elements (20+) — listing page with many dated items
        if (signals.TimeElementCount >= 20)
        {
            score -= 10;
        }

        // === Classify based on score ===
        PageClassification classification;
        if (score >= ArticleThreshold)
        {
            classification = PageClassification.Article;
        }
        else if (score <= LinkListThreshold)
        {
            classification = PageClassification.LinkList;
        }
        else
        {
            classification = PageClassification.Unknown;
        }

        return (classification, score);
    }

    /// <summary>
    /// Legacy classifier — kept for backward compatibility during transition.
    /// Delegates to ClassifyScored when signals are available.
    /// </summary>
    public static PageClassification Classify(
        IReadOnlyList<LinkInfo> links,
        bool isArticlePage,
        int articleContainerCount,
        string url)
    {
        var contentLinks = 0;
        foreach (var link in links)
        {
            if (link.Type == LinkType.Content)
            {
                contentLinks++;
            }
        }

        // Rule 1: Section/homepage URLs are always link lists if they have any content links.
        if (IsSectionUrlPattern(url) && contentLinks >= 1)
        {
            return PageClassification.LinkList;
        }

        // Rule 2: Article URL pattern (date slug) + article HTML structure + single container.
        if (IsArticleUrlPattern(url) && isArticlePage && articleContainerCount <= 1)
        {
            return PageClassification.Article;
        }

        // Rule 3: Many content links + multiple article containers = index/section page.
        if (contentLinks >= 10 && articleContainerCount >= 2)
        {
            return PageClassification.LinkList;
        }

        // Rule 4: Article HTML structure with single container.
        if (isArticlePage && articleContainerCount <= 1)
        {
            return PageClassification.Article;
        }

        // Rule 5: Many content links without article structure.
        if (contentLinks >= 10)
        {
            return PageClassification.LinkList;
        }

        return PageClassification.Unknown;
    }

    private static bool IsArticleLdJsonType(string type) =>
        type.Contains("Article", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("BlogPosting", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("NewsArticle", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("ReportageNewsArticle", StringComparison.OrdinalIgnoreCase);

    private static bool IsIndexLdJsonType(string type) =>
        type is "WebSite" or "CollectionPage" or "SearchResultsPage" or "ItemList";

    private static bool IsRootUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath.TrimEnd('/');
            return string.IsNullOrEmpty(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a URL is a section index or homepage — NOT an article under a section.
    /// Matches root paths, bare section paths (/science), and one-level sub-sections
    /// (/section/world), but NOT deep paths like /science/915244/slug which are articles.
    /// </summary>
    public static bool IsSectionUrlPattern(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.TrimEnd('/');

            // Root path is always a section/index page
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            // Split path into segments: "/science/915244/slug" → ["science", "915244", "slug"]
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return true;
            }

            // First segment must be a known section keyword
            if (!SectionKeywords.Contains(segments[0]))
            {
                return false;
            }

            // Bare section path (/science) — always a section index
            if (segments.Length == 1)
            {
                return true;
            }

            // One sub-level (/section/world, /tag/javascript) — still a section
            // BUT: if the second segment is a numeric ID, it's likely an article
            // (e.g., /science/915244 on The Verge)
            if (segments.Length == 2 && !IsNumericOrSlug(segments[1]))
            {
                return true;
            }

            // 3+ segments (/science/915244/spacex-ipo) — too deep, likely an article
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a path segment looks like a numeric article ID or a content slug
    /// rather than a sub-section name.
    /// </summary>
    private static bool IsNumericOrSlug(string segment)
    {
        // Pure numeric = article ID (e.g., "915244" in /science/915244)
        if (long.TryParse(segment, out _))
        {
            return true;
        }

        // Contains digits mixed with hyphens = content slug (e.g., "24038888-meta-threads")
        if (segment.Length > 10 && segment.Any(char.IsDigit) && segment.Contains('-'))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a URL looks like an article path (contains date patterns).
    /// Used as a positive article signal in classification.
    /// </summary>
    public static bool IsArticleUrlPattern(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        try
        {
            var path = new Uri(url).AbsolutePath;

            // Match date patterns: /YYYY/MM/DD/ or /YYYY/MM/
            return ArticleDateRegex().IsMatch(path);
        }
        catch
        {
            return false;
        }
    }

    private static readonly HashSet<string> SectionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "section", "topic", "tag", "category", "categories", "latest", "archive",
        "search", "live", "series", "trending", "popular", "opinion", "world",
        "politics", "business", "technology", "science", "health", "sports", "arts",
        "style", "food", "travel", "magazine", "books", "podcasts", "video", "news",
        "tech", "entertainment", "reviews", "features", "culture", "money", "climate",
        "education", "real-estate", "nyregion", "briefing", "interactive", "us", "uk",
    };

    [GeneratedRegex(@"/\d{4}/\d{2}/(\d{2}/)?", RegexOptions.Compiled)]
    private static partial Regex ArticleDateRegex();
}
