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
    /// Checks if a URL matches common section/index page patterns.
    /// Used as the primary signal in classification (rule 1) and for
    /// cache quality gates.
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

            // Known section path prefixes
            return SectionPathRegex().IsMatch(path);
        }
        catch
        {
            return false;
        }
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

    [GeneratedRegex(
        @"^/(section|topic|tag|category|categories|latest|archive|search|live|series|trending|popular|opinion|world|politics|business|technology|science|health|sports|arts|style|food|travel|magazine|books|podcasts|video|news|tech|entertainment|reviews|features|culture|money|climate|education|real-estate|nyregion|briefing|interactive|us|uk)(/|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SectionPathRegex();

    [GeneratedRegex(@"/\d{4}/\d{2}/(\d{2}/)?", RegexOptions.Compiled)]
    private static partial Regex ArticleDateRegex();
}
