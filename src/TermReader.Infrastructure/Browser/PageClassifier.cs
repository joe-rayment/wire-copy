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
    public const int ClassificationVersion = 3;

    /// <summary>
    /// Classifies a page using URL-first logic with HTML tiebreakers.
    /// </summary>
    /// <param name="links">Links extracted by ILinkExtractor.</param>
    /// <param name="isArticlePage">Result of ReadableContentExtractor.IsArticlePage(html).</param>
    /// <param name="articleContainerCount">Count of &lt;article&gt; elements in the HTML.</param>
    /// <param name="url">Final page URL (post-redirect).</param>
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
        // This fires FIRST — URL shape is stable across sites and prevents article
        // heuristics from misclassifying homepages with featured stories.
        if (IsSectionUrlPattern(url) && contentLinks >= 1)
        {
            return PageClassification.LinkList;
        }

        // Rule 2: Article URL pattern (date slug) + article HTML structure + single container.
        // Strong article signal: /YYYY/MM/DD/ paths are almost always articles.
        if (IsArticleUrlPattern(url) && isArticlePage && articleContainerCount <= 1)
        {
            return PageClassification.Article;
        }

        // Rule 3: Many content links + multiple article containers = index/section page.
        // News fronts use one <article> per story card.
        if (contentLinks >= 10 && articleContainerCount >= 2)
        {
            return PageClassification.LinkList;
        }

        // Rule 4: Article HTML structure with single container, any number of inline links.
        // Protects link-heavy articles (Wikipedia, Verge longform) from being
        // misclassified as LinkList by the content-link-count rule below.
        if (isArticlePage && articleContainerCount <= 1)
        {
            return PageClassification.Article;
        }

        // Rule 5: Many content links without article structure.
        // Pages with 10+ content links and no article indicators are link lists.
        if (contentLinks >= 10)
        {
            return PageClassification.LinkList;
        }

        // Rule 6: Conservative default — Unknown pages show hierarchical view
        // without auto-switching to readable. User can still press 'v'.
        return PageClassification.Unknown;
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
