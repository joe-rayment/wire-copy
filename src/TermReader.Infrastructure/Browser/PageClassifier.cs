// Educational and personal use only.

using System.Text.RegularExpressions;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Classifies pages as Article, LinkList, or Unknown based on signals
/// already computed during link extraction. No extra HTML parse needed.
/// </summary>
internal static partial class PageClassifier
{
    /// <summary>
    /// Classifies a page using existing extraction outputs.
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

        // Strong article signal: page has article structure AND few content links
        if (isArticlePage && contentLinks <= 10)
        {
            return PageClassification.Article;
        }

        // Strong article signal: page has article structure even with sidebar links
        if (isArticlePage && contentLinks > 10 && articleContainerCount <= 1)
        {
            return PageClassification.Article;
        }

        // Strong link-list signal: many content links AND multiple article containers
        // (news front pages use one <article> per story card)
        if (contentLinks >= 15 && articleContainerCount >= 3)
        {
            return PageClassification.LinkList;
        }

        // Strong link-list signal: many content links AND not an article page
        if (contentLinks >= 15 && !isArticlePage)
        {
            return PageClassification.LinkList;
        }

        // URL pattern hints for ambiguous cases
        if (IsSectionUrlPattern(url))
        {
            return contentLinks >= 5 ? PageClassification.LinkList : PageClassification.Unknown;
        }

        // Conservative default
        return PageClassification.Unknown;
    }

    /// <summary>
    /// Checks if a URL matches common section/index page patterns.
    /// Used for pre-classification (before HTML is available) and as a
    /// tiebreaker signal in the main classifier.
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

    [GeneratedRegex(
        @"^/(section|topic|tag|category|categories|latest|archive|search|live|series|trending|popular|opinion|world|politics|business|technology|science|health|sports|arts|style|food|travel|magazine|books|podcasts|video)(/|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SectionPathRegex();
}
